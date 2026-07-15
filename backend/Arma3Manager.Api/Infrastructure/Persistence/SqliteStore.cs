using System.Text.Json;
using Arma3Manager.Api.Application;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Security;
using Microsoft.Data.Sqlite;

namespace Arma3Manager.Api.Infrastructure.Persistence;

/// <summary>SQLite persistence gateway for manager settings, mods, lists, and authentication metadata.</summary>
public sealed class SqliteStore(string dbPath)
{
    public string DbPath => dbPath;
    readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    SqliteConnection Open() { Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!); var connection = new SqliteConnection($"Data Source={dbPath}"); connection.Open(); return connection; }

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
        """;
        await command.ExecuteNonQueryAsync();
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
