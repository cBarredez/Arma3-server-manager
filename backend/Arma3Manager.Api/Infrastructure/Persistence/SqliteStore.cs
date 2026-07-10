using System.Text.Json;
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
        """;
        await command.ExecuteNonQueryAsync();
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
    public async Task<Mod?> SetModActiveAsync(string id, bool active)
    {
        var mods = await GetModsAsync(); var mod = mods.FirstOrDefault(item => item.Id == id); if (mod is null) return null;
        mod = mod with { Active = active }; await ReplaceModsAsync(mods.Select(item => item.Id == id ? mod : item)); return mod;
    }
    public async Task UpsertModAsync(Mod mod)
    {
        var mods = await GetModsAsync(); mods.RemoveAll(item => item.WorkshopId == mod.WorkshopId || Path.GetFullPath(item.Path) == Path.GetFullPath(mod.Path)); mods.Add(mod); await ReplaceModsAsync(mods);
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
        await SetRawStateAsync("active-modlist", id); var state = await GetModlistsAsync(); var active = state.Lists.FirstOrDefault(list => list.Id == id);
        if (active is not null) { var mods = await GetModsAsync(); var ids = active.Mods.Select(mod => mod.WorkshopId).ToHashSet(); await ReplaceModsAsync(mods.Select(mod => mod.WorkshopId is null ? mod : mod with { Active = ids.Contains(mod.WorkshopId) })); }
        return state;
    }
    public async Task DeleteModlistAsync(string id) { await using var connection = Open(); var command = connection.CreateCommand(); command.CommandText = "delete from modlists where id=$id"; command.Parameters.AddWithValue("$id", id); await command.ExecuteNonQueryAsync(); }
    public async Task<int> DeleteModsForModlistAsync(string id)
    {
        var list = (await GetModlistsAsync()).Lists.FirstOrDefault(item => item.Id == id); if (list is null) return 0; var ids = list.Mods.Select(mod => mod.WorkshopId).ToHashSet(); var mods = await GetModsAsync(); var deleting = mods.Where(mod => mod.WorkshopId is not null && ids.Contains(mod.WorkshopId)).ToList();
        foreach (var mod in deleting) if (Directory.Exists(mod.Path)) Directory.Delete(mod.Path, true); await ReplaceModsAsync(mods.Except(deleting)); return deleting.Count;
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
    async Task<string?> GetRawAsync(string table, string key) { await using var connection = Open(); var command = connection.CreateCommand(); command.CommandText = $"select value from {table} where key=$key"; command.Parameters.AddWithValue("$key", key); return (string?)await command.ExecuteScalarAsync(); }
    async Task SetRawAsync(string table, string key, string value) { await using var connection = Open(); var command = connection.CreateCommand(); command.CommandText = $"insert into {table}(key,value) values($key,$value) on conflict(key) do update set value=excluded.value"; command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value); await command.ExecuteNonQueryAsync(); }
}
