using System.Text.Json;
using Arma3Manager.Api.Application;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Security;
using Microsoft.Data.Sqlite;

namespace Arma3Manager.Api.Infrastructure.Persistence;

public sealed record PersistedServerRuntime(
    string Phase,
    string? Stage,
    string? OperationId,
    string? OwnerInstanceId,
    string? RunId,
    int? Pid,
    long? ProcessStartedAtUtcTicks,
    string? BinaryPath,
    DateTimeOffset Since,
    DateTimeOffset UpdatedAt,
    string? LastError,
    int[] ConflictingPids);

/// <summary>SQLite persistence gateway for manager settings, mods, lists, and authentication metadata.</summary>
public sealed class SqliteStore(string dbPath)
{
    public string DbPath => dbPath;
    readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "pragma foreign_keys=on; pragma busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task InitAsync()
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
        create table if not exists settings (key text primary key, value text not null);
        create table if not exists mods (id text primary key, name text not null, path text not null, active integer not null, workshop_id text null);
        create table if not exists modlists (id text primary key, name text not null, mods_json text not null, created_at text not null);
        create table if not exists app_state (key text primary key, value text not null);
        create table if not exists task_history (id integer primary key autoincrement, kind text not null, command text not null, exit_code integer null, created_at text not null);
        create table if not exists file_index (path text primary key, parent text not null, name text not null, is_dir integer not null, size integer not null, created_utc text not null, mtime_utc text not null, scan_gen integer not null);
        create index if not exists ix_file_index_parent on file_index(parent);
        create table if not exists metrics_samples (id integer primary key autoincrement, run_id text not null, sampled_at text not null, cpu_percent real null, cores_capacity real not null, memory_used_bytes integer not null, memory_percent real not null);
        create index if not exists ix_metrics_samples_run on metrics_samples(run_id, sampled_at);
        create table if not exists schema_migrations (version integer primary key, applied_at text not null);
        create table if not exists server_sessions (
            run_id text primary key, started_at text not null, ended_at text null, pid integer null,
            exit_code integer null, end_reason text not null default 'running', created_at text not null
        );
        create index if not exists ix_server_sessions_started on server_sessions(started_at desc, run_id);
        create table if not exists server_runtime (
            singleton_id integer primary key check(singleton_id=1),
            phase text not null, stage text null, operation_id text null, owner_instance_id text null,
            run_id text null, pid integer null, process_started_ticks integer null, binary_path text null,
            since text not null, updated_at text not null, last_error text null,
            conflicting_pids_json text not null default '[]'
        );
        create table if not exists player_connections (
            id integer primary key autoincrement, run_id text not null references server_sessions(run_id) on delete cascade,
            first_seen_at text not null, admitted_at text null, ended_at text null,
            outcome text not null, active integer not null default 1,
            network_id text null, steam_uid text null, battleye_guid text null, rcon_player_id integer null,
            name text null, ip text null, reason_code text null, reason_text text null,
            source text not null, confidence text not null
        );
        create index if not exists ix_player_connections_run on player_connections(run_id, first_seen_at desc, id desc);
        create index if not exists ix_player_connections_filters on player_connections(run_id, outcome, reason_code);
        create index if not exists ix_player_connections_network on player_connections(run_id, network_id);
        create index if not exists ix_player_connections_guid on player_connections(run_id, battleye_guid);
        create table if not exists player_events (
            id integer primary key autoincrement, connection_id integer null references player_connections(id) on delete cascade,
            run_id text not null references server_sessions(run_id) on delete cascade, occurred_at text not null,
            kind text not null, reason_code text null, reason_text text null, source text not null,
            confidence text not null, raw_text text not null, dedupe_key text not null unique
        );
        create index if not exists ix_player_events_connection on player_events(connection_id, occurred_at, id);
        create index if not exists ix_player_events_run on player_events(run_id, occurred_at desc, id desc);
        """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = "pragma journal_mode=wal;";
        await command.ExecuteScalarAsync();
        command.CommandText = """
        insert or ignore into server_sessions(run_id,started_at,ended_at,pid,exit_code,end_reason,created_at)
        select run_id,min(sampled_at),max(sampled_at),null,null,'legacy_inferred',min(sampled_at)
        from metrics_samples group by run_id;
        update server_sessions set ended_at=$now,end_reason='superseded'
        where ended_at is null and end_reason='running' and run_id not in (
            select run_id from server_sessions where ended_at is null and end_reason='running'
            order by started_at desc limit 1
        );
        create unique index if not exists ux_server_sessions_one_running
        on server_sessions(end_reason) where ended_at is null and end_reason='running';
        insert or ignore into server_runtime(singleton_id,phase,since,updated_at,conflicting_pids_json)
        values(1,'stopped',$now,$now,'[]');
        insert or ignore into schema_migrations(version,applied_at) values(1,$now);
        """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
        await ApplyPlayerTrackingV2MigrationAsync(connection);
        await ApplyHeadlessClientHistoryMigrationAsync(connection);
        await ApplyNaturalPlayerIdentityMigrationAsync(connection);
    }

    public async Task<PersistedServerRuntime> GetServerRuntimeAsync()
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select phase,stage,operation_id,owner_instance_id,run_id,pid,process_started_ticks,binary_path,since,updated_at,last_error,conflicting_pids_json from server_runtime where singleton_id=1";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Server runtime row is missing");
        return ReadServerRuntime(reader);
    }

    public async Task<(bool Accepted, PersistedServerRuntime State)> TryReserveServerStartAsync(string operationId, string ownerInstanceId, DateTimeOffset now)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
        update server_runtime set phase='preparing',stage='validating',operation_id=$operation,owner_instance_id=$owner,
            run_id=null,pid=null,process_started_ticks=null,binary_path=null,since=$now,updated_at=$now,last_error=null,conflicting_pids_json='[]'
        where singleton_id=1 and phase in ('stopped','faulted');
        """;
        command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue("$owner", ownerInstanceId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        var accepted = await command.ExecuteNonQueryAsync() == 1;
        var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "select phase,stage,operation_id,owner_instance_id,run_id,pid,process_started_ticks,binary_path,since,updated_at,last_error,conflicting_pids_json from server_runtime where singleton_id=1";
        await using var reader = await read.ExecuteReaderAsync();
        await reader.ReadAsync();
        var state = ReadServerRuntime(reader);
        await transaction.CommitAsync();
        return (accepted, state);
    }

    public async Task<bool> UpdateServerRuntimeAsync(PersistedServerRuntime state, string? expectedOperationId = null)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
        update server_runtime set phase=$phase,stage=$stage,operation_id=$operation,owner_instance_id=$owner,
            run_id=$run,pid=$pid,process_started_ticks=$ticks,binary_path=$binary,since=$since,updated_at=$updated,
            last_error=$error,conflicting_pids_json=$conflicts
        where singleton_id=1
        """ + (expectedOperationId is null ? "" : " and operation_id=$expected");
        command.Parameters.AddWithValue("$phase", state.Phase);
        command.Parameters.AddWithValue("$stage", (object?)state.Stage ?? DBNull.Value);
        command.Parameters.AddWithValue("$operation", (object?)state.OperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$owner", (object?)state.OwnerInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", (object?)state.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$pid", (object?)state.Pid ?? DBNull.Value);
        command.Parameters.AddWithValue("$ticks", (object?)state.ProcessStartedAtUtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$binary", (object?)state.BinaryPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$since", state.Since.ToString("O"));
        command.Parameters.AddWithValue("$updated", state.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$error", (object?)state.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$conflicts", JsonSerializer.Serialize(state.ConflictingPids, json));
        if (expectedOperationId is not null) command.Parameters.AddWithValue("$expected", expectedOperationId);
        return await command.ExecuteNonQueryAsync() == 1;
    }

    public async Task<ServerSessionSummary?> GetOpenServerSessionAsync()
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
        select s.run_id,s.started_at,s.ended_at,s.pid,s.exit_code,s.end_reason,
               0,0,0,0,0
        from server_sessions s where s.ended_at is null and s.end_reason='running'
        order by s.started_at desc limit 1
        """;
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadServerSession(reader) : null;
    }

    public async Task CloseOpenServerSessionsAsync(string reason, string? exceptRunId = null)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "update server_sessions set ended_at=$ended,end_reason=$reason where ended_at is null and end_reason='running'" +
            (exceptRunId is null ? "" : " and run_id<>$except");
        command.Parameters.AddWithValue("$ended", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$reason", reason);
        if (exceptRunId is not null) command.Parameters.AddWithValue("$except", exceptRunId);
        await command.ExecuteNonQueryAsync();
    }

    static PersistedServerRuntime ReadServerRuntime(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        DateTimeOffset.Parse(reader.GetString(8)),
        DateTimeOffset.Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        JsonSerializer.Deserialize<int[]>(reader.GetString(11)) ?? []);

    static async Task ApplyPlayerTrackingV2MigrationAsync(SqliteConnection connection)
    {
        var applied = connection.CreateCommand();
        applied.CommandText = "select 1 from schema_migrations where version=2 limit 1";
        if (await applied.ExecuteScalarAsync() is not null) return;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = """
        update player_connections set network_id=null
        where id in (
            select distinct connection_id from player_events
            where connection_id is not null and raw_text like '%Error in expression%'
              and raw_text like '%A3MGR_PLAYER_V%'
        ) and (instr(coalesce(network_id,''),'%1') > 0 or instr(coalesce(network_id,''),'_this') > 0
               or instr(coalesce(network_id,''),'];>') > 0);
        delete from player_events
        where raw_text like '%Error in expression%' and raw_text like '%A3MGR_PLAYER_V%';
        delete from player_connections
        where not exists (select 1 from player_events e where e.connection_id=player_connections.id);
        insert into schema_migrations(version,applied_at) values(2,$now);
        """;
        cleanup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cleanup.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    static async Task ApplyHeadlessClientHistoryMigrationAsync(SqliteConnection connection)
    {
        var applied = connection.CreateCommand();
        applied.CommandText = "select 1 from schema_migrations where version=3 limit 1";
        if (await applied.ExecuteScalarAsync() is not null) return;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = """
        delete from player_events
        where id in (
            select e.id from player_events e
            join player_connections p on p.id=e.connection_id
            where e.kind='rejected' and e.reason_code='wrong_password' and e.source='arma'
              and lower(e.raw_text) like '%cannot join the session. wrong password was given.%'
              and p.network_id is null and p.steam_uid is null and p.battleye_guid is null
              and p.rcon_player_id is null and p.name is null and p.ip is null
        );
        delete from player_connections
        where not exists (select 1 from player_events e where e.connection_id=player_connections.id);
        insert into schema_migrations(version,applied_at) values(3,$now);
        """;
        cleanup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cleanup.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    static async Task ApplyNaturalPlayerIdentityMigrationAsync(SqliteConnection connection)
    {
        var applied = connection.CreateCommand();
        applied.CommandText = "select 1 from schema_migrations where version=4 limit 1";
        if (await applied.ExecuteScalarAsync() is not null) return;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
        select e.connection_id,e.raw_text,e.run_id,e.occurred_at,e.source
        from player_events e join player_connections p on p.id=e.connection_id
        where e.connection_id is not null and p.steam_uid is null
          and lower(e.raw_text) like '%player %'
          and (lower(e.raw_text) like '% connected (id=%' or lower(e.raw_text) like '% disconnected (id=%')
        """;
        var repairs = new List<(long ConnectionId, string SteamUid, string Name)>();
        await using (var reader = await select.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var at = DateTimeOffset.TryParse(reader.GetString(3), out var parsed) ? parsed : DateTimeOffset.UtcNow;
                var entry = new LogEntry("migration", reader.GetString(1), at, Source: reader.GetString(4), RunId: reader.GetString(2));
                if (PlayerSignalParser.TryParse(entry, out var signal) && signal?.SteamUid is { } steamUid && signal.Name is { } name)
                    repairs.Add((reader.GetInt64(0), steamUid, name));
            }
        }

        foreach (var repair in repairs)
        {
            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "update player_connections set steam_uid=coalesce(steam_uid,$steam),name=coalesce(name,$name) where id=$id";
            update.Parameters.AddWithValue("$steam", repair.SteamUid);
            update.Parameters.AddWithValue("$name", repair.Name);
            update.Parameters.AddWithValue("$id", repair.ConnectionId);
            await update.ExecuteNonQueryAsync();
        }

        var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "insert into schema_migrations(version,applied_at) values(4,$now)";
        mark.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await mark.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    // Records one CPU/RAM sample for the currently running game session (identified by RuntimeState.RunId),
    // so "how much CPU/RAM did this match use" can be reconstructed and exported after the fact instead of
    // only being visible live on the dashboard while the match is running.
    public async Task InsertMetricsSampleAsync(MetricsSample sample)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "insert into metrics_samples(run_id, sampled_at, cpu_percent, cores_capacity, memory_used_bytes, memory_percent) values ($runId,$sampledAt,$cpu,$cores,$memBytes,$memPercent)";
        command.Parameters.AddWithValue("$runId", sample.RunId);
        command.Parameters.AddWithValue("$sampledAt", sample.SampledAt.ToString("O"));
        command.Parameters.AddWithValue("$cpu", (object?)sample.CpuPercent ?? DBNull.Value);
        command.Parameters.AddWithValue("$cores", sample.CoresCapacity);
        command.Parameters.AddWithValue("$memBytes", sample.MemoryUsedBytes);
        command.Parameters.AddWithValue("$memPercent", sample.MemoryPercent);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<MetricsSessionSummary>> GetMetricsSessionsAsync(string? currentRunId, int limit = 50)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
        select run_id, min(sampled_at), max(sampled_at), count(*), avg(cpu_percent), max(cpu_percent), max(cores_capacity), avg(memory_percent), max(memory_percent)
        from metrics_samples
        group by run_id
        order by min(sampled_at) desc
        limit $limit
        """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        var results = new List<MetricsSessionSummary>();
        while (await reader.ReadAsync())
        {
            var runId = reader.GetString(0);
            var startedAt = DateTimeOffset.Parse(reader.GetString(1));
            var lastSampleAt = DateTimeOffset.Parse(reader.GetString(2));
            var ongoing = runId == currentRunId;
            results.Add(new(
                runId, startedAt, ongoing ? null : lastSampleAt, reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8)));
        }
        return results;
    }

    public async Task<List<MetricsSample>> GetMetricsSamplesAsync(string runId)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select run_id, sampled_at, cpu_percent, cores_capacity, memory_used_bytes, memory_percent from metrics_samples where run_id = $runId order by sampled_at";
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync();
        var results = new List<MetricsSample>();
        while (await reader.ReadAsync())
        {
            results.Add(new(
                reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.GetDouble(3), reader.GetInt64(4), reader.GetDouble(5)));
        }
        return results;
    }

    // Keeps the table from growing forever on a long-lived server — only the most recent sessions are kept
    // for CSV export; older ones are dropped wholesale (never partially truncated, so a kept session's history
    // stays complete).
    public async Task PruneMetricsSessionsAsync(int keepSessions = 30)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
        delete from metrics_samples where run_id not in (
            select run_id from metrics_samples group by run_id order by min(sampled_at) desc limit $keep
        )
        """;
        command.Parameters.AddWithValue("$keep", keepSessions);
        await command.ExecuteNonQueryAsync();
    }

    public async Task StartServerSessionAsync(ServerRunStarted run)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
        insert into server_sessions(run_id,started_at,ended_at,pid,exit_code,end_reason,created_at)
        values($run,$started,null,$pid,null,'running',$started)
        on conflict(run_id) do update set started_at=excluded.started_at,ended_at=null,pid=excluded.pid,exit_code=null,end_reason='running'
        """;
        command.Parameters.AddWithValue("$run", run.RunId);
        command.Parameters.AddWithValue("$started", run.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$pid", run.Pid);
        await command.ExecuteNonQueryAsync();
    }

    public async Task EndServerSessionAsync(ServerRunEnded run)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
        update server_sessions set ended_at=coalesce(ended_at,$ended),exit_code=coalesce(exit_code,$exit),
            end_reason=case when end_reason='running' then $reason else end_reason end
        where run_id=$run;
        update player_connections set active=0,ended_at=coalesce(ended_at,$ended)
        where run_id=$run and active=1;
        """;
        command.Parameters.AddWithValue("$run", run.RunId);
        command.Parameters.AddWithValue("$ended", run.EndedAt.ToString("O"));
        command.Parameters.AddWithValue("$exit", (object?)run.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", run.Reason);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task<bool> ApplyPlayerSignalAsync(PlayerSignal signal)
    {
        return await ApplyPlayerSignalsAsync([signal]) == 1;
    }

    public async Task<int> ApplyPlayerSignalsAsync(IReadOnlyList<PlayerSignal> signals)
    {
        if (signals.Count == 0) return 0;
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        var persisted = 0;
        foreach (var signal in signals)
            if (await ApplyPlayerSignalAsync(connection, transaction, signal)) persisted++;
        await transaction.CommitAsync();
        return persisted;
    }

    static async Task<bool> ApplyPlayerSignalAsync(SqliteConnection connection, SqliteTransaction transaction, PlayerSignal signal)
    {
        var dedupe = connection.CreateCommand();
        dedupe.Transaction = transaction;
        dedupe.CommandText = "select 1 from player_events where dedupe_key=$key limit 1";
        dedupe.Parameters.AddWithValue("$key", signal.DedupeKey);
        if (await dedupe.ExecuteScalarAsync() is not null) return false;

        var ensureSession = connection.CreateCommand();
        ensureSession.Transaction = transaction;
        ensureSession.CommandText = """
        insert or ignore into server_sessions(run_id,started_at,ended_at,pid,exit_code,end_reason,created_at)
        values($run,$at,null,null,null,'running',$at)
        """;
        ensureSession.Parameters.AddWithValue("$run", signal.RunId);
        ensureSession.Parameters.AddWithValue("$at", signal.OccurredAt.ToString("O"));
        await ensureSession.ExecuteNonQueryAsync();

        var existing = await FindConnectionAsync(connection, transaction, signal);
        var outcome = PlayerOutcomes.Resolve(signal, existing);
        var admittedAt = signal.Admitted || signal.Kind is "connected" or "rcon_seen"
            ? signal.OccurredAt
            : existing?.AdmittedAt;
        var active = !signal.Terminal;
        long connectionId;

        if (existing is null)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
            insert into player_connections(run_id,first_seen_at,admitted_at,ended_at,outcome,active,
                network_id,steam_uid,battleye_guid,rcon_player_id,name,ip,reason_code,reason_text,source,confidence)
            values($run,$at,$admitted,$ended,$outcome,$active,$network,$steam,$guid,$rcon,$name,$ip,$reasonCode,$reasonText,$source,$confidence);
            select last_insert_rowid();
            """;
            AddSignalParameters(insert, signal);
            insert.Parameters.AddWithValue("$admitted", admittedAt is null ? DBNull.Value : admittedAt.Value.ToString("O"));
            insert.Parameters.AddWithValue("$ended", signal.Terminal ? signal.OccurredAt.ToString("O") : DBNull.Value);
            insert.Parameters.AddWithValue("$outcome", outcome);
            insert.Parameters.AddWithValue("$active", active ? 1 : 0);
            connectionId = (long)(await insert.ExecuteScalarAsync())!;
        }
        else
        {
            connectionId = existing.Id;
            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
            update player_connections set
                admitted_at=coalesce(admitted_at,$admitted), ended_at=case when $terminal=1 then $at else ended_at end,
                outcome=$outcome, active=case when $terminal=1 then 0 else 1 end,
                network_id=coalesce($network,network_id),steam_uid=coalesce($steam,steam_uid),
                battleye_guid=coalesce($guid,battleye_guid),rcon_player_id=coalesce($rcon,rcon_player_id),
                name=coalesce($name,name),ip=coalesce($ip,ip),
                reason_code=coalesce($reasonCode,reason_code),reason_text=coalesce($reasonText,reason_text),
                source=case when $confidence='authoritative' then $source else source end,
                confidence=case when $confidence='authoritative' then $confidence else confidence end
            where id=$id
            """;
            AddSignalParameters(update, signal);
            update.Parameters.AddWithValue("$admitted", admittedAt is null ? DBNull.Value : admittedAt.Value.ToString("O"));
            update.Parameters.AddWithValue("$terminal", signal.Terminal ? 1 : 0);
            update.Parameters.AddWithValue("$outcome", outcome);
            update.Parameters.AddWithValue("$id", connectionId);
            await update.ExecuteNonQueryAsync();
        }

        var eventCommand = connection.CreateCommand();
        eventCommand.Transaction = transaction;
        eventCommand.CommandText = """
        insert into player_events(connection_id,run_id,occurred_at,kind,reason_code,reason_text,source,confidence,raw_text,dedupe_key)
        values($connection,$run,$at,$kind,$reasonCode,$reasonText,$source,$confidence,$raw,$key)
        """;
        eventCommand.Parameters.AddWithValue("$connection", connectionId);
        eventCommand.Parameters.AddWithValue("$run", signal.RunId);
        eventCommand.Parameters.AddWithValue("$at", signal.OccurredAt.ToString("O"));
        eventCommand.Parameters.AddWithValue("$kind", signal.Kind);
        eventCommand.Parameters.AddWithValue("$reasonCode", (object?)signal.ReasonCode ?? DBNull.Value);
        eventCommand.Parameters.AddWithValue("$reasonText", (object?)signal.ReasonText ?? DBNull.Value);
        eventCommand.Parameters.AddWithValue("$source", signal.Source);
        eventCommand.Parameters.AddWithValue("$confidence", signal.Confidence);
        eventCommand.Parameters.AddWithValue("$raw", signal.RawText);
        eventCommand.Parameters.AddWithValue("$key", signal.DedupeKey);
        await eventCommand.ExecuteNonQueryAsync();
        return true;
    }

    static void AddSignalParameters(SqliteCommand command, PlayerSignal signal)
    {
        command.Parameters.AddWithValue("$run", signal.RunId);
        command.Parameters.AddWithValue("$at", signal.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$network", (object?)signal.NetworkId ?? DBNull.Value);
        command.Parameters.AddWithValue("$steam", (object?)signal.SteamUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$guid", (object?)signal.BattlEyeGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("$rcon", (object?)signal.RconPlayerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)signal.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$ip", (object?)signal.Ip ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasonCode", (object?)signal.ReasonCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasonText", (object?)signal.ReasonText ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", signal.Source);
        command.Parameters.AddWithValue("$confidence", signal.Confidence);
    }

    static async Task<PlayerConnectionRecord?> FindConnectionAsync(SqliteConnection connection, SqliteTransaction transaction, PlayerSignal signal)
    {
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(signal.NetworkId)) conditions.Add("network_id=$network");
        if (!string.IsNullOrWhiteSpace(signal.SteamUid)) conditions.Add("steam_uid=$steam");
        if (!string.IsNullOrWhiteSpace(signal.BattlEyeGuid)) conditions.Add("battleye_guid=$guid");
        if (signal.RconPlayerId is not null) conditions.Add("rcon_player_id=$rcon");
        if (!string.IsNullOrWhiteSpace(signal.Name) && !string.IsNullOrWhiteSpace(signal.Ip)) conditions.Add("(name=$name and ip=$ip)");
        if (conditions.Count == 0) return null;

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var terminalWindow = signal.Kind is "connected" or "rcon_seen"
            ? "and active=1"
            : "and (active=1 or ended_at >= $cutoff)";
        command.CommandText = $"""
        select id,run_id,first_seen_at,admitted_at,ended_at,outcome,active,network_id,steam_uid,battleye_guid,
               rcon_player_id,name,ip,reason_code,reason_text,source,confidence
        from player_connections where run_id=$run and ({string.Join(" or ", conditions)})
        {terminalWindow}
        order by active desc,first_seen_at desc,id desc limit 1
        """;
        AddSignalParameters(command, signal);
        command.Parameters.AddWithValue("$cutoff", signal.OccurredAt.AddSeconds(-30).ToString("O"));
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync()) return ReadPlayerConnection(reader);
        await reader.DisposeAsync();

        // The server hook can know Arma's network id, Steam UID, and name before RCon knows the IP/GUID.
        // Enrich only when one compatible partial connection is waiting; ambiguity creates a separate row.
        if (signal.Kind != "rcon_seen") return null;
        var fallback = connection.CreateCommand();
        fallback.Transaction = transaction;
        fallback.CommandText = """
        select id,run_id,first_seen_at,admitted_at,ended_at,outcome,active,network_id,steam_uid,battleye_guid,
               rcon_player_id,name,ip,reason_code,reason_text,source,confidence
        from player_connections where run_id=$run and active=1 and first_seen_at >= $cutoff
          and battleye_guid is null and ip is null and (name is null or name=$name)
        order by first_seen_at desc,id desc limit 2
        """;
        fallback.Parameters.AddWithValue("$run", signal.RunId);
        fallback.Parameters.AddWithValue("$cutoff", signal.OccurredAt.AddSeconds(-30).ToString("O"));
        fallback.Parameters.AddWithValue("$name", (object?)signal.Name ?? DBNull.Value);
        var candidates = new List<PlayerConnectionRecord>();
        await using var fallbackReader = await fallback.ExecuteReaderAsync();
        while (await fallbackReader.ReadAsync()) candidates.Add(ReadPlayerConnection(fallbackReader));
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public async Task<PagedResult<ServerSessionSummary>> GetServerSessionsAsync(DateTimeOffset? from, DateTimeOffset? to, string? status, string? cursor, int limit)
    {
        limit = Math.Clamp(limit, 1, 100);
        var offset = int.TryParse(cursor, out var parsed) && parsed >= 0 ? parsed : 0;
        await using var connection = Open();
        var command = connection.CreateCommand();
        var filters = new List<string>();
        if (from is not null) filters.Add("s.started_at >= $from");
        if (to is not null) filters.Add("s.started_at <= $to");
        if (status == "running") filters.Add("s.ended_at is null");
        else if (status == "ended") filters.Add("s.ended_at is not null");
        command.CommandText = $"""
        select s.run_id,s.started_at,s.ended_at,s.pid,s.exit_code,s.end_reason,
          sum(case when p.outcome='successful' then 1 else 0 end),
          sum(case when p.outcome='rejected' then 1 else 0 end),
          sum(case when p.outcome='removed' then 1 else 0 end),
          sum(case when p.outcome='pending' then 1 else 0 end),
          count(distinct coalesce(p.steam_uid,p.battleye_guid,p.network_id,p.name,p.ip,cast(p.id as text)))
        from server_sessions s left join player_connections p on p.run_id=s.run_id
        {(filters.Count == 0 ? "" : "where " + string.Join(" and ", filters))}
        group by s.run_id order by s.started_at desc,s.run_id desc limit $take offset $offset
        """;
        command.Parameters.AddWithValue("$take", limit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        if (from is not null) command.Parameters.AddWithValue("$from", from.Value.ToString("O"));
        if (to is not null) command.Parameters.AddWithValue("$to", to.Value.ToString("O"));
        var results = new List<ServerSessionSummary>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) results.Add(ReadServerSession(reader));
        var hasMore = results.Count > limit;
        if (hasMore) results.RemoveAt(results.Count - 1);
        return new(results, hasMore ? (offset + limit).ToString() : null);
    }

    public async Task<ServerSessionSummary?> GetServerSessionAsync(string runId)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
        select s.run_id,s.started_at,s.ended_at,s.pid,s.exit_code,s.end_reason,
          sum(case when p.outcome='successful' then 1 else 0 end),sum(case when p.outcome='rejected' then 1 else 0 end),
          sum(case when p.outcome='removed' then 1 else 0 end),sum(case when p.outcome='pending' then 1 else 0 end),
          count(distinct coalesce(p.steam_uid,p.battleye_guid,p.network_id,p.name,p.ip,cast(p.id as text)))
        from server_sessions s left join player_connections p on p.run_id=s.run_id where s.run_id=$run group by s.run_id
        """;
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadServerSession(reader) : null;
    }

    public async Task<PagedResult<PlayerConnectionRecord>> GetPlayerConnectionsAsync(string? runId, string? outcome, string? reasonCode, string? query, string? cursor, int limit)
    {
        limit = Math.Clamp(limit, 1, 100);
        var offset = int.TryParse(cursor, out var parsed) && parsed >= 0 ? parsed : 0;
        await using var connection = Open();
        var command = connection.CreateCommand();
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(runId)) filters.Add("run_id=$run");
        if (!string.IsNullOrWhiteSpace(outcome)) filters.Add("outcome=$outcome");
        if (!string.IsNullOrWhiteSpace(reasonCode)) filters.Add("reason_code=$reason");
        if (!string.IsNullOrWhiteSpace(query)) filters.Add("(name like $query or ip like $query or steam_uid like $query or battleye_guid like $query or reason_text like $query)");
        command.CommandText = $"""
        select id,run_id,first_seen_at,admitted_at,ended_at,outcome,active,network_id,steam_uid,battleye_guid,
               rcon_player_id,name,ip,reason_code,reason_text,source,confidence
        from player_connections {(filters.Count == 0 ? "" : "where " + string.Join(" and ", filters))}
        order by first_seen_at desc,id desc limit $take offset $offset
        """;
        command.Parameters.AddWithValue("$take", limit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$run", runId ?? "");
        command.Parameters.AddWithValue("$outcome", outcome ?? "");
        command.Parameters.AddWithValue("$reason", reasonCode ?? "");
        command.Parameters.AddWithValue("$query", $"%{query}%");
        var results = new List<PlayerConnectionRecord>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) results.Add(ReadPlayerConnection(reader));
        var hasMore = results.Count > limit;
        if (hasMore) results.RemoveAt(results.Count - 1);
        return new(results, hasMore ? (offset + limit).ToString() : null);
    }

    public async Task<List<PlayerEventRecord>> GetPlayerEventsAsync(long connectionId)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select id,connection_id,run_id,occurred_at,kind,reason_code,reason_text,source,confidence,raw_text from player_events where connection_id=$id order by occurred_at,id";
        command.Parameters.AddWithValue("$id", connectionId);
        var results = new List<PlayerEventRecord>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) results.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9)));
        return results;
    }

    public async Task PruneHistoryAsync(int retentionDays)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
        delete from metrics_samples where sampled_at < $cutoff;
        delete from server_sessions where coalesce(ended_at,started_at) < $cutoff;
        """;
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    static ServerSessionSummary ReadServerSession(SqliteDataReader reader) => new(
        reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetString(5),
        reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10));

    static PlayerConnectionRecord ReadPlayerConnection(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
        reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
        reader.GetString(5), reader.GetInt32(6) != 0, reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14), reader.GetString(15), reader.GetString(16));
    public async Task<bool> EnsureFileIndexVersionAsync(int version)
    {
        var expected = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (await GetRawStateAsync("file-index-version") == expected) return false;
        await using var connection = Open();
        await using var transaction = connection.BeginTransaction();
        var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "delete from file_index";
        await clear.ExecuteNonQueryAsync();
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "insert into app_state(key,value) values('file-index-version',$version) on conflict(key) do update set value=excluded.value";
        update.Parameters.AddWithValue("$version", expected);
        await update.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return true;
    }
    public async Task MigrateJsonStateAsync(ServerPaths paths)
    {
        var startup = Path.Combine(paths.Arma3Dir, "startup.json");
        if (File.Exists(startup) && await GetRawSettingAsync("startup") is null) await SetRawSettingAsync("startup", await File.ReadAllTextAsync(startup));
        var auth = Path.Combine(paths.Arma3Dir, "steamcmd-auth.json");
        if (File.Exists(auth) && await GetRawStateAsync("steamcmd-auth") is null) await SetRawStateAsync("steamcmd-auth", await File.ReadAllTextAsync(auth));
    }
    public async Task<StartupSettings> GetStartupAsync(ServerPaths paths, AppConfig config)
    {
        var raw = await GetRawSettingAsync("startup");
        return raw is null ? StartupSettings.Default(paths, config) : JsonSerializer.Deserialize<StartupSettings>(raw, json)!.Normalized(paths, config);
    }
    public Task SaveStartupAsync(StartupSettings settings) => SetRawSettingAsync("startup", JsonSerializer.Serialize(settings, json));
    public async Task<PanelAuth> GetPanelAuthAsync(AppConfig config)
    {
        var raw = await GetRawStateAsync("panel-auth");
        return raw is null ? PasswordHasher.Create(config.WebUsername, config.WebPassword) : JsonSerializer.Deserialize<PanelAuth>(raw, json)!;
    }
    public Task SavePanelAuthAsync(PanelAuth auth) => SetRawStateAsync("panel-auth", JsonSerializer.Serialize(auth, json));
    public async Task<SteamAuth?> GetSteamAuthAsync() { var raw = await GetRawStateAsync("steamcmd-auth"); return raw is null ? null : JsonSerializer.Deserialize<SteamAuth>(raw, json); }
    public Task SaveSteamAuthAsync(string username) => SetRawStateAsync("steamcmd-auth", JsonSerializer.Serialize(new SteamAuth(username, DateTimeOffset.UtcNow), json));

    public async Task<List<CreatorDlc>> GetCreatorDlcsAsync(AppConfig config)
    {
        var active = await GetActiveCreatorDlcIdsAsync();
        return CreatorDlcCatalog.List(config).Select(dlc => dlc with { Active = active.Contains(dlc.Id) && dlc.Available }).ToList();
    }
    public async Task<CreatorDlc?> SetCreatorDlcActiveAsync(AppConfig config, string id, bool active)
    {
        var selected = CreatorDlcCatalog.List(config).FirstOrDefault(dlc => dlc.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (selected is null) return null;
        var activeIds = await GetActiveCreatorDlcIdsAsync();
        activeIds.RemoveWhere(value => value.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (active && selected.Available) activeIds.Add(selected.Id);
        await SetRawStateAsync("creator-dlcs-active", JsonSerializer.Serialize(activeIds.Order().ToArray(), json));
        return selected with { Active = active && selected.Available };
    }
    public async Task<List<string>> GetActiveCreatorDlcPathsAsync(AppConfig config) => (await GetCreatorDlcsAsync(config)).Where(dlc => dlc.Active && dlc.Available).Select(dlc => dlc.Path).ToList();
    async Task<HashSet<string>> GetActiveCreatorDlcIdsAsync()
    {
        var raw = await GetRawStateAsync("creator-dlcs-active");
        return raw is null ? new(StringComparer.OrdinalIgnoreCase) : new(JsonSerializer.Deserialize<string[]>(raw, json) ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<Mod>> SyncAndGetModsAsync(string root)
    {
        var mods = (await GetModsAsync()).Where(mod => string.IsNullOrWhiteSpace(mod.Path) || Directory.Exists(mod.Path)).ToList();
        foreach (var directory in Directory.Exists(root) ? Directory.EnumerateDirectories(root, "@*") : [])
            if (mods.All(mod => Path.GetFullPath(mod.Path) != Path.GetFullPath(directory))) mods.Add(new(Guid.NewGuid().ToString("n"), Path.GetFileName(directory), directory, true, null));
        await ReplaceModsAsync(mods);
        return mods;
    }
    public async Task<List<string>> GetActiveModsAsync() => (await GetModsAsync()).Where(mod => mod.Active).Select(mod => mod.Path).ToList();
    public async Task<List<Mod>> GetActiveWorkshopModsAsync() => (await GetModsAsync())
        .Where(mod => mod.Active && !string.IsNullOrWhiteSpace(mod.WorkshopId))
        .GroupBy(mod => mod.WorkshopId, StringComparer.Ordinal)
        .Select(group => group.First())
        .ToList();
    public async Task<Mod?> SetModActiveAsync(string id, bool active)
    {
        var mods = await GetModsAsync(); var mod = mods.FirstOrDefault(item => item.Id == id); if (mod is null) return null;
        mod = mod with { Active = active }; await ReplaceModsAsync(mods.Select(item => item.Id == id ? mod : item)); return mod;
    }
    public async Task UpsertModAsync(Mod mod)
    {
        var mods = await GetModsAsync(); mods.RemoveAll(item => item.WorkshopId == mod.WorkshopId || Path.GetFullPath(item.Path) == Path.GetFullPath(mod.Path)); mods.Add(mod); await ReplaceModsAsync(mods);
    }
    public async Task NormalizeWorkshopModPathsAsync(AppConfig config)
    {
        var mods = await GetModsAsync();
        var workshopIds = mods.Where(mod => !string.IsNullOrWhiteSpace(mod.WorkshopId)).Select(mod => mod.WorkshopId!).ToHashSet(StringComparer.Ordinal);
        var normalized = mods
            .Where(mod => mod.WorkshopId is not null || !workshopIds.Contains(Path.GetFileName(mod.Path).TrimStart('@')))
            .Select(mod => mod.WorkshopId is null || !WorkshopStorage.IsInstalled(config, mod.WorkshopId)
                ? mod
                : mod with { Path = Directory.Exists(WorkshopStorage.Reference(config, mod.WorkshopId))
                    ? WorkshopStorage.Reference(config, mod.WorkshopId)
                    : WorkshopStorage.Source(config, mod.WorkshopId) })
            .GroupBy(mod => mod.WorkshopId ?? $"path:{Path.GetFullPath(mod.Path)}")
            .Select(group => group.First())
            .ToList();
        await ReplaceModsAsync(normalized);
    }
    public async Task<Mod?> DeleteModAsync(string id)
    {
        var mods = await GetModsAsync(); var mod = mods.FirstOrDefault(item => item.Id == id); if (mod is null) return null; mods.RemoveAll(item => item.Id == id); await ReplaceModsAsync(mods); return mod;
    }
    public async Task<int> RemoveModsForDeletedPathAsync(string target)
    {
        var targetFull = Path.GetFullPath(target); var mods = await GetModsAsync();
        var keep = mods.Where(mod => { var full = Path.GetFullPath(mod.Path); return !full.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase) && !targetFull.StartsWith(full, StringComparison.OrdinalIgnoreCase); }).ToList();
        await ReplaceModsAsync(keep); return mods.Count - keep.Count;
    }

    public async Task<ModlistState> GetModlistsAsync()
    {
        await using var connection = Open(); var lists = new List<Modlist>(); var command = connection.CreateCommand(); command.CommandText = "select id,name,mods_json,created_at from modlists order by created_at desc";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lists.Add(new(reader.GetString(0), reader.GetString(1), JsonSerializer.Deserialize<List<PresetMod>>(reader.GetString(2), json) ?? [], DateTimeOffset.Parse(reader.GetString(3))));
        return new(await GetRawStateAsync("active-modlist"), lists);
    }
    public async Task<Modlist> SaveModlistAsync(ModlistSaveRequest request)
    {
        var list = new Modlist(Guid.NewGuid().ToString("n"), string.IsNullOrWhiteSpace(request.Name) ? $"Modlist {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}" : request.Name, request.Mods, DateTimeOffset.UtcNow);
        await using var connection = Open(); var command = connection.CreateCommand(); command.CommandText = "insert into modlists(id,name,mods_json,created_at) values($id,$name,$mods,$created)";
        command.Parameters.AddWithValue("$id", list.Id); command.Parameters.AddWithValue("$name", list.Name); command.Parameters.AddWithValue("$mods", JsonSerializer.Serialize(list.Mods, json)); command.Parameters.AddWithValue("$created", list.CreatedAt.ToString("O")); await command.ExecuteNonQueryAsync();
        if (request.Activate) await ActivateModlistAsync(list.Id); return list;
    }
    public async Task<ModlistState> ActivateModlistAsync(string id)
    {
        var state = await GetModlistsAsync();
        var active = state.Lists.FirstOrDefault(list => list.Id == id);
        if (active is null) return state;
        await SetRawStateAsync("active-modlist", id);
        var mods = await GetModsAsync();
        var ids = active.Mods.Select(mod => mod.WorkshopId).ToHashSet();
        await ReplaceModsAsync(mods.Select(mod => mod.WorkshopId is null ? mod : mod with { Active = ids.Contains(mod.WorkshopId) }));
        return await GetModlistsAsync();
    }
    public async Task<ModlistState> DeactivateModlistAsync(string id)
    {
        var state = await GetModlistsAsync();
        var active = state.Lists.FirstOrDefault(list => list.Id == id);
        if (active is null || state.ActiveModlistId != id) return state;
        var ids = active.Mods.Select(mod => mod.WorkshopId).ToHashSet();
        var mods = await GetModsAsync();
        await ReplaceModsAsync(mods.Select(mod => mod.WorkshopId is not null && ids.Contains(mod.WorkshopId) ? mod with { Active = false } : mod));
        await DeleteRawStateAsync("active-modlist");
        return await GetModlistsAsync();
    }
    public async Task DeleteModlistAsync(string id)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "delete from modlists where id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
        if (await GetRawStateAsync("active-modlist") == id) await DeleteRawStateAsync("active-modlist");
    }
    public async Task<List<Mod>> DeleteModsForModlistAsync(string id)
    {
        var list = (await GetModlistsAsync()).Lists.FirstOrDefault(item => item.Id == id);
        if (list is null) return [];
        var ids = list.Mods.Select(mod => mod.WorkshopId).ToHashSet();
        var mods = await GetModsAsync();
        var deleting = mods.Where(mod => mod.WorkshopId is not null && ids.Contains(mod.WorkshopId)).ToList();
        await ReplaceModsAsync(mods.Except(deleting));
        return deleting;
    }
    async Task<List<Mod>> GetModsAsync()
    {
        await using var connection = Open(); var command = connection.CreateCommand(); command.CommandText = "select id,name,path,active,workshop_id from mods"; var result = new List<Mod>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1, reader.IsDBNull(4) ? null : reader.GetString(4))); return result;
    }
    async Task ReplaceModsAsync(IEnumerable<Mod> mods)
    {
        await using var connection = Open(); await using var transaction = await connection.BeginTransactionAsync(); var delete = connection.CreateCommand(); delete.CommandText = "delete from mods"; await delete.ExecuteNonQueryAsync();
        foreach (var mod in mods) { var command = connection.CreateCommand(); command.CommandText = "insert into mods(id,name,path,active,workshop_id) values($id,$name,$path,$active,$wid)"; command.Parameters.AddWithValue("$id", mod.Id); command.Parameters.AddWithValue("$name", mod.Name); command.Parameters.AddWithValue("$path", mod.Path); command.Parameters.AddWithValue("$active", mod.Active ? 1 : 0); command.Parameters.AddWithValue("$wid", (object?)mod.WorkshopId ?? DBNull.Value); await command.ExecuteNonQueryAsync(); }
        await transaction.CommitAsync();
    }
    Task<string?> GetRawSettingAsync(string key) => GetRawAsync("settings", key);
    Task SetRawSettingAsync(string key, string value) => SetRawAsync("settings", key, value);
    Task<string?> GetRawStateAsync(string key) => GetRawAsync("app_state", key);
    Task SetRawStateAsync(string key, string value) => SetRawAsync("app_state", key, value);
    async Task DeleteRawStateAsync(string key)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "delete from app_state where key=$key";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync();
    }
    async Task<string?> GetRawAsync(string table, string key) { await using var connection = Open(); var command = connection.CreateCommand(); command.CommandText = $"select value from {table} where key=$key"; command.Parameters.AddWithValue("$key", key); return (string?)await command.ExecuteScalarAsync(); }
    async Task SetRawAsync(string table, string key, string value) { await using var connection = Open(); var command = connection.CreateCommand(); command.CommandText = $"insert into {table}(key,value) values($key,$value) on conflict(key) do update set value=excluded.value"; command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value); await command.ExecuteNonQueryAsync(); }

    // ── File index (watchdog-maintained metadata cache for the File Manager) ──────
    public async Task<Dictionary<string, (string Parent, DateTime MTime, long Size)>> GetIndexedDirectoriesAsync()
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select path, parent, mtime_utc, size from file_index where is_dir = 1";
        var result = new Dictionary<string, (string, DateTime, long)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = (reader.GetString(1), DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.GetInt64(3));
        return result;
    }

    public async Task<long> GetNextFileIndexScanGenerationAsync()
    {
        var raw = await GetRawStateAsync("file-index-scan-gen");
        var next = (raw is null ? 0L : long.Parse(raw)) + 1;
        await SetRawStateAsync("file-index-scan-gen", next.ToString());
        return next;
    }

    // `visitedDirs` must contain only directories whose entries were actually re-enumerated this cycle
    // (not ones merely confirmed unchanged via the mtime fast-path) — reconciliation deletion is scoped
    // to those directories' children only, since an unchanged directory's children were never re-checked
    // for existence and must not be swept away just because their row wasn't touched this cycle.
    public async Task ApplyFileIndexScanAsync(IReadOnlyList<FileIndexRow> upserts, IReadOnlyList<string> visitedDirs, long scanGen)
    {
        await using var connection = Open();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var row in upserts)
        {
            var command = connection.CreateCommand();
            command.CommandText = """
            insert into file_index(path, parent, name, is_dir, size, created_utc, mtime_utc, scan_gen)
            values ($path, $parent, $name, $isDir, $size, $created, $mtime, $gen)
            on conflict(path) do update set parent=excluded.parent, name=excluded.name, is_dir=excluded.is_dir,
                size=excluded.size, created_utc=excluded.created_utc, mtime_utc=excluded.mtime_utc, scan_gen=excluded.scan_gen
            """;
            command.Parameters.AddWithValue("$path", row.Path);
            command.Parameters.AddWithValue("$parent", row.Parent);
            command.Parameters.AddWithValue("$name", row.Name);
            command.Parameters.AddWithValue("$isDir", row.IsDir ? 1 : 0);
            command.Parameters.AddWithValue("$size", row.Size);
            command.Parameters.AddWithValue("$created", row.Created.ToString("O"));
            command.Parameters.AddWithValue("$mtime", row.MTime.ToString("O"));
            command.Parameters.AddWithValue("$gen", scanGen);
            await command.ExecuteNonQueryAsync();
        }
        if (visitedDirs.Count > 0)
        {
            var delete = connection.CreateCommand();
            var parameterNames = visitedDirs.Select((_, i) => $"$p{i}").ToArray();
            delete.CommandText = $"delete from file_index where scan_gen < $gen and parent in ({string.Join(",", parameterNames)})";
            delete.Parameters.AddWithValue("$gen", scanGen);
            for (var i = 0; i < visitedDirs.Count; i++) delete.Parameters.AddWithValue(parameterNames[i], visitedDirs[i]);
            await delete.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<FileItem[]?> GetFileIndexChildrenAsync(string relDir)
    {
        var normalized = NormalizeRelative(relDir);
        await using var connection = Open();
        var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "select count(*) from file_index where path = $path";
        existsCommand.Parameters.AddWithValue("$path", normalized);
        var exists = Convert.ToInt64(await existsCommand.ExecuteScalarAsync()) > 0;
        if (!exists) return null;

        var command = connection.CreateCommand();
        // path != parent excludes the root's own self-referential row (path='', parent='') from appearing
        // as a child of itself when listing the root directory.
        command.CommandText = "select name, path, is_dir, size, created_utc, mtime_utc from file_index where parent = $parent and path != $parent order by is_dir desc, name";
        command.Parameters.AddWithValue("$parent", normalized);
        var items = new List<FileItem>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var isDir = reader.GetInt32(2) == 1;
            var created = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind);
            var modified = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind);
            items.Add(new FileItem(reader.GetString(0), reader.GetString(1), isDir, reader.GetInt64(3), modified, created));
        }
        return items.ToArray();
    }

    public async Task<long?> GetIndexedRootSizeAsync()
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select size from file_index where path = ''";
        var result = await command.ExecuteScalarAsync();
        return result is null ? null : Convert.ToInt64(result);
    }

    public async Task<Dictionary<string, long>> GetIndexedDirectorySizesAsync(string relParent)
    {
        var normalized = NormalizeRelative(relParent);
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "select name, size from file_index where parent=$parent and is_dir=1";
        command.Parameters.AddWithValue("$parent", normalized);
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) sizes[reader.GetString(0)] = reader.GetInt64(1);
        return sizes;
    }

    public async Task<Dictionary<string, long>> GetRootDisplayDirectorySizesAsync()
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
        select case when parent = '' then name else '@' || name end as display_name, size, parent
        from file_index
        where is_dir = 1 and parent in ('', 'steamapps/workshop/content/107410')
        order by case when parent = '' then 0 else 1 end
        """;
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) sizes[reader.GetString(0)] = reader.GetInt64(1);
        return sizes;
    }

    public async Task RemoveFileIndexForDeletedPathAsync(string relPath)
    {
        var normalized = NormalizeRelative(relPath);
        if (normalized.Length == 0) return; // never remove the root row this way
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
        delete from file_index where path = $path
            or (length(path) > length($path) and substr(path, 1, length($path) + 1) = $path || '/')
        """;
        command.Parameters.AddWithValue("$path", normalized);
        await command.ExecuteNonQueryAsync();
    }

    public async Task InvalidateFileIndexDirAsync(string relDir)
    {
        var normalized = NormalizeRelative(relDir);
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "delete from file_index where path = $path";
        command.Parameters.AddWithValue("$path", normalized);
        await command.ExecuteNonQueryAsync();
    }

    static string NormalizeRelative(string? relPath) => (relPath ?? "").Replace('\\', '/').Trim('/');
}
