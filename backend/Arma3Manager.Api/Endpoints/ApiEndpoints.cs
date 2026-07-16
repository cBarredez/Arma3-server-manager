using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arma3Manager.Api.Application;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Infrastructure;
using Arma3Manager.Api.Infrastructure.Persistence;
using Arma3Manager.Api.Security;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Arma3Manager.Api.Endpoints;

/// <summary>Registers the stable REST and authentication surface of the manager.</summary>
public static class ApiEndpoints
{
    /// <summary>Maps all existing API routes without changing their public contracts.</summary>
    public static void MapApiEndpoints(this WebApplication app, AppConfig cfg, ServerPaths paths, SqliteStore store)
    {
        app.MapGet("/api/health", () => Results.Json(new { status = "ok", backend = "dotnet-kestrel", sqlite = store.DbPath }));
        
        app.MapPost("/api/auth/login", async (HttpContext http, LoginRequest req) =>
        {
            var auth = await store.GetPanelAuthAsync(cfg);
            if (req.Username == auth.Username && PasswordHasher.Verify(req.Password, auth.PasswordSalt, auth.PasswordHash))
            {
                http.Session.SetString("authenticated", "true");
                http.Session.SetString("authProof", SessionProof.Create(cfg.SessionSecret));
                http.Session.SetString("authMethod", "password");
                return Results.Json(new { ok = true });
            }
            return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
        });
        
        app.MapPost("/api/auth/logout", (HttpContext http) =>
        {
            http.Session.Clear();
            return Results.Json(new { ok = true });
        });
        
        app.MapGet("/api/auth/check", (HttpContext http) => Results.Json(new
        {
            authenticated = IsAuthed(http, cfg),
            method = http.Session.GetString("authMethod"),
            steamId = http.Session.GetString("steamId")
        }));
        
        app.MapGet("/auth/steam", (HttpContext http) =>
        {
            var baseUrl = BaseUrl(http, cfg);
            var query = new Dictionary<string, string?>
            {
                ["openid.ns"] = "http://specs.openid.net/auth/2.0",
                ["openid.mode"] = "checkid_setup",
                ["openid.return_to"] = $"{baseUrl}/auth/steam/return",
                ["openid.realm"] = baseUrl,
                ["openid.identity"] = "http://specs.openid.net/auth/2.0/identifier_select",
                ["openid.claimed_id"] = "http://specs.openid.net/auth/2.0/identifier_select"
            };
            return Results.Redirect("https://steamcommunity.com/openid/login?" + QueryString.Create(query));
        });
        
        app.MapGet("/auth/steam/return", async (HttpContext http) =>
        {
            var form = http.Request.Query.ToDictionary(k => k.Key, v => v.Value.ToString());
            if (!form.TryGetValue("openid.mode", out var mode) || mode != "id_res")
                return Results.Redirect("/?steam_error=cancelled");
        
            form["openid.mode"] = "check_authentication";
            using var client = new HttpClient();
            using var content = new FormUrlEncodedContent(form);
            var text = await (await client.PostAsync("https://steamcommunity.com/openid/login", content)).Content.ReadAsStringAsync();
            if (!text.Contains("is_valid:true", StringComparison.OrdinalIgnoreCase))
                return Results.Redirect("/?steam_error=invalid");
        
            var claimed = http.Request.Query["openid.claimed_id"].ToString();
            var match = Regex.Match(claimed, @"/id/(\d+)$");
            if (!match.Success) return Results.Redirect("/?steam_error=no_id");
            var steamId = match.Groups[1].Value;
            if (cfg.SteamOwnerIds.Count > 0 && !cfg.SteamOwnerIds.Contains(steamId))
                return Results.Redirect("/?steam_error=unauthorized");
        
            http.Session.SetString("authenticated", "true");
            http.Session.SetString("authProof", SessionProof.Create(cfg.SessionSecret));
            http.Session.SetString("authMethod", "steam");
            http.Session.SetString("steamId", steamId);
            return Results.Redirect("/");
        });
        
        var api = app.MapGroup("/api").AddEndpointFilter(AuthFilter);
        
        api.MapGet("/steam/status", async () =>
        {
            var auth = await store.GetSteamAuthAsync();
            var user = ResolvedSteamUser(cfg, auth);
            var hasCachedLogin = SteamCmdSession.HasCachedLogin(user);
            return Results.Json(new
            {
                hasCredentials = !string.IsNullOrWhiteSpace(user),
                hasPassword = !string.IsNullOrWhiteSpace(cfg.SteamPass),
                hasCachedLogin,
                requiresLogin = !hasCachedLogin,
                user = string.IsNullOrWhiteSpace(user) ? "anonymous" : user,
                steamcmd = paths.SteamCmd,
                login = SteamCmdSession.EmptyPublicState()
            });
        });
        
        api.MapGet("/startup", async () =>
        {
            var settings = await store.GetStartupAsync(paths, cfg);
            var mods = await store.GetActiveCreatorDlcPathsAsync(cfg);
            return Results.Json(new { settings, command = CommandBuilder.Build(paths, settings, mods) });
        });
        
        api.MapPut("/startup", async (StartupSettings settings, RuntimeState runtime) =>
        {
            settings = settings.Normalized(paths, cfg);
            await store.SaveStartupAsync(settings);
            await ServerCfgWriter.ApplyAsync(settings);
            var mods = await store.GetActiveCreatorDlcPathsAsync(cfg);
            return Results.Json(new { settings, command = CommandBuilder.Build(paths, settings, mods) });
        });

        api.MapGet("/missions", async () =>
        {
            var settings = await store.GetStartupAsync(paths, cfg);
            return Results.Json(new { selected = MissionConfig.ReadSelected(settings.ServerCfg), missions = MissionConfig.List(paths) });
        });

        api.MapPut("/startup/mission", async (MissionSelectionRequest req, ServerLifecycleCoordinator lifecycle) =>
        {
            if (LifecycleOwnsServer(await lifecycle.GetStatusAsync())) return Results.Json(new { error = "Stop or cancel the game server before changing the mission" }, statusCode: 409);
            var template = req.Template.EndsWith(".pbo", StringComparison.OrdinalIgnoreCase) ? req.Template[..^4] : req.Template;
            var mission = MissionConfig.List(paths).FirstOrDefault(item => item.Template.Equals(template, StringComparison.OrdinalIgnoreCase));
            if (mission is null) return Results.Json(new { error = "Mission not found in mpmissions" }, statusCode: 404);
            var settings = await store.GetStartupAsync(paths, cfg);
            await MissionConfig.ApplyAsync(settings.ServerCfg, mission.Template);
            return Results.Json(new { ok = true, selected = mission.Template });
        });
        
        api.MapGet("/server/status", async (RuntimeState runtime, ServerLifecycleCoordinator lifecycle, PlayerActivityService activity) =>
        {
            var startup = await store.GetStartupAsync(paths, cfg);
            var status = await lifecycle.GetStatusAsync();
            var joinHost = !string.IsNullOrWhiteSpace(cfg.PublicJoinHost)
                ? cfg.PublicJoinHost
                : Uri.TryCreate(cfg.BaseUrl, UriKind.Absolute, out var baseUri) ? baseUri.Host : "";
            return Results.Json(new
            {
                status.Phase, status.Running, status.Busy, status.Stage, status.OperationId, status.Since,
                status.LastError, status.Managed, status.Pid, status.RunId, status.ConflictingPids,
                port = startup.Port, joinHost, headlessClientPids = runtime.HeadlessClientPids, playerTracking = activity.State
            });
        });
        
        api.MapPost("/server/start", async (RuntimeState runtime, ServerLifecycleCoordinator lifecycle, PlayerActivityService activity) =>
            await StartServerAsync(cfg, paths, store, runtime, lifecycle, activity));

        api.MapPost("/server/stop", async (ServerLifecycleCoordinator lifecycle, BattlEyeRconClient rcon) =>
        {
            var result = await lifecycle.StopAsync("stopped");
            if (!result.Accepted)
                return Results.Json(new { error = result.Error, code = result.Code, status = result.Status }, statusCode: 409);
            await rcon.DisconnectAsync();
            return Results.Json(new { ok = true, status = result.Status });
        });

        api.MapPost("/server/restart", async (RuntimeState runtime, ServerLifecycleCoordinator lifecycle, BattlEyeRconClient rcon, PlayerActivityService activity) =>
        {
            var result = await StartServerAsync(cfg, paths, store, runtime, lifecycle, activity, restart: true);
            if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status202Accepted }) await rcon.DisconnectAsync();
            return result;
        });

        api.MapPost("/server/rcon/command", async (RconCommandRequest req, RuntimeState runtime, BattlEyeRconClient rcon) =>
        {
            if (!runtime.IsRunning) return Results.Json(new { error = "Server is not running" }, statusCode: 400);
            try { return Results.Json(new { ok = true, response = await rcon.SendCommandAsync(req.Command) }); }
            catch (Exception exception) { return Results.Json(new { error = exception.Message }, statusCode: 502); }
        });

        api.MapPost("/server/rcon/say", async (RconSayRequest req, RuntimeState runtime, BattlEyeRconClient rcon) =>
        {
            if (!runtime.IsRunning) return Results.Json(new { error = "Server is not running" }, statusCode: 400);
            try { return Results.Json(new { ok = true, response = await rcon.SendSayAsync(req.Message) }); }
            catch (Exception exception) { return Results.Json(new { error = exception.Message }, statusCode: 502); }
        });

        api.MapGet("/server/rcon/players", async (RuntimeState runtime, BattlEyeRconClient rcon) =>
        {
            if (!runtime.IsRunning) return Results.Json(new { error = "Server is not running" }, statusCode: 400);
            try { return Results.Json(await rcon.GetPlayersAsync()); }
            catch (Exception exception) { return Results.Json(new { error = exception.Message }, statusCode: 502); }
        });

        api.MapPost("/server/rcon/kick", async (HttpContext http, RconKickRequest req, RuntimeState runtime, BattlEyeRconClient rcon, PlayerActivityService activity) =>
        {
            if (!runtime.IsRunning) return Results.Json(new { error = "Server is not running" }, statusCode: 400);
            try
            {
                var player = (await rcon.GetPlayersAsync()).FirstOrDefault(item => item.Id == req.PlayerId);
                var response = await rcon.KickAsync(req.PlayerId, req.Reason);
                activity.RecordManagerAction("kicked", player, req.Reason, response, LogicalOperator(http));
                return Results.Json(new { ok = true, response });
            }
            catch (Exception exception) { return Results.Json(new { error = exception.Message }, statusCode: 502); }
        });

        api.MapPost("/server/rcon/ban", async (HttpContext http, RconBanRequest req, RuntimeState runtime, BattlEyeRconClient rcon, PlayerActivityService activity) =>
        {
            if (!runtime.IsRunning) return Results.Json(new { error = "Server is not running" }, statusCode: 400);
            try
            {
                var player = (await rcon.GetPlayersAsync()).FirstOrDefault(item => item.Id == req.PlayerId);
                var response = await rcon.BanAsync(req.PlayerId, req.Minutes, req.Reason);
                activity.RecordManagerAction("banned", player, req.Reason, response, LogicalOperator(http));
                return Results.Json(new { ok = true, response });
            }
            catch (Exception exception) { return Results.Json(new { error = exception.Message }, statusCode: 502); }
        });

        api.MapGet("/sessions", async (DateTimeOffset? from, DateTimeOffset? to, string? status, string? cursor, int? limit) =>
            Results.Json(await store.GetServerSessionsAsync(from, to, status, cursor, limit ?? 25)));

        api.MapGet("/sessions/{runId}", async (string runId) =>
        {
            var session = await store.GetServerSessionAsync(runId);
            return session is null ? Results.Json(new { error = "Session not found" }, statusCode: 404) : Results.Json(session);
        });

        api.MapGet("/player-connections", async (string? runId, string? outcome, string? reasonCode, string? q, string? cursor, int? limit) =>
            Results.Json(await store.GetPlayerConnectionsAsync(runId, outcome, reasonCode, q, cursor, limit ?? 50)));

        api.MapGet("/player-connections/{id:long}/events", async (long id) =>
            Results.Json(await store.GetPlayerEventsAsync(id)));

        api.MapPost("/server/install", async (RuntimeState runtime) =>
        {
            var auth = await store.GetSteamAuthAsync();
            var user = ResolvedSteamUser(cfg, auth);
            if (!SteamCmdSession.HasCachedLogin(user)) return SteamLoginRequired(user);
            var startup = await store.GetStartupAsync(paths, cfg);
            var args = SteamArgs(cfg, auth, ServerUpdateArgs(cfg, startup));
            if (!runtime.RunTask(paths.SteamCmd, ["+force_install_dir", cfg.Arma3Dir, .. args, "+quit"], "install", dedupeKey: "server-maintenance"))
                return Results.Json(new { error = "A server installation or update is already running" }, statusCode: 409);
            return Results.Json(new { ok = true, message = "Server installation started" });
        });
        
        api.MapPost("/server/update", async (RuntimeState runtime) =>
        {
            var auth = await store.GetSteamAuthAsync();
            var user = ResolvedSteamUser(cfg, auth);
            if (!SteamCmdSession.HasCachedLogin(user)) return SteamLoginRequired(user);
            var startup = await store.GetStartupAsync(paths, cfg);
            var args = SteamArgs(cfg, auth, ServerUpdateArgs(cfg, startup));
            if (!runtime.RunTask(paths.SteamCmd, ["+force_install_dir", cfg.Arma3Dir, .. args, "+quit"], "update", dedupeKey: "server-maintenance"))
                return Results.Json(new { error = "A server installation or update is already running" }, statusCode: 409);
            return Results.Json(new { ok = true, message = "Update started" });
        });
        
        api.MapPost("/server/download-creator-dlcs", async (RuntimeState runtime) =>
        {
            var auth = await store.GetSteamAuthAsync();
            var user = ResolvedSteamUser(cfg, auth);
            if (!SteamCmdSession.HasCachedLogin(user)) return SteamLoginRequired(user);
            var args = SteamArgs(cfg, auth, CreatorDlcDownloadArgs(cfg));
            if (!runtime.RunTask(paths.SteamCmd, ["+force_install_dir", cfg.Arma3Dir, .. args, "+quit"], "creator-dlcs"))
                return Results.Json(new { error = "Creator DLC download is already running" }, statusCode: 409);
            return Results.Json(new
            {
                ok = true,
                message = "Creator DLC download started",
                configuredDlcAppIds = cfg.CreatorDlcAppIds
            });
        });
        
        api.MapGet("/creator-dlcs", async () => Results.Json(await store.GetCreatorDlcsAsync(cfg)));
        api.MapPut("/creator-dlcs/{id}", async (string id, CreatorDlcUpdate req) =>
        {
            var dlc = await store.SetCreatorDlcActiveAsync(cfg, id, req.Active);
            return dlc is null ? Results.Json(new { error = "Creator DLC not found" }, statusCode: 404) : Results.Json(dlc);
        });
        
        api.MapGet("/metrics", async (MetricsSampler sampler) =>
        {
            var memory = MetricsReader.ReadMemory();
            var sample = sampler.Current;
            var trackedBytes = await store.GetIndexedRootSizeAsync();
            return Results.Json(new
            {
                cpu = sample.Cpu,
                memory,
                disk = new[] { MetricsReader.ReadDisk(paths.Arma3Dir, "/arma3") },
                trackedDiskUsage = trackedBytes is null ? null : new { bytes = trackedBytes.Value },
                temperature = sample.Temperature,
                network = Array.Empty<object>()
            });
        });
        
        // Backs "Añadir métricas solicitadas" (issue #12): per-match CPU/RAM history, listed here and
        // downloadable as CSV. A "session" is one game-server run, identified by RuntimeState.RunId; the
        // MetricsSampler background service records a sample every 5s into metrics_samples while it's running.
        api.MapGet("/metrics/sessions", async (RuntimeState runtime) =>
            Results.Json(await store.GetMetricsSessionsAsync(runtime.IsRunning ? runtime.RunId : null)));

        api.MapGet("/metrics/sessions/{runId}/csv", async (string runId) =>
        {
            var samples = await store.GetMetricsSamplesAsync(runId);
            if (samples.Count == 0) return Results.Json(new { error = "No metrics recorded for that session" }, statusCode: 404);

            var csv = new StringBuilder();
            csv.AppendLine("timestamp_utc,cpu_percent,cores_capacity,memory_used_mb,memory_percent");
            foreach (var sample in samples)
            {
                csv.Append(sample.SampledAt.UtcDateTime.ToString("O")).Append(',')
                    .Append(sample.CpuPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "").Append(',')
                    .Append(sample.CoresCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append((sample.MemoryUsedBytes / 1024d / 1024d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(sample.MemoryPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
            }
            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return Results.File(bytes, "text/csv", $"session-{runId}.csv");
        });

        api.MapGet("/mods", async () => Results.Json(await store.SyncAndGetModsAsync(cfg.Arma3Dir)));
        api.MapPut("/mods/{id}", async (string id, ModUpdate req) =>
        {
            var mod = await store.SetModActiveAsync(id, req.Active);
            return mod is null ? Results.Json(new { error = "Mod not found" }, statusCode: 404) : Results.Json(mod);
        });
        api.MapDelete("/mods/{id}", async (string id) =>
        {
            var mod = await store.DeleteModAsync(id);
            if (mod is null) return Results.Json(new { error = "Mod not found" }, statusCode: 404);
            await DeleteModFilesAndIndexAsync(cfg, store, mod);
            return Results.Json(new { ok = true });
        });
        api.MapPost("/mods/install", async (InstallModRequest req, RuntimeState runtime) =>
        {
            var presetMod = new PresetMod(req.Name ?? $"@{req.WorkshopId}", req.WorkshopId);
            return await QueueWorkshopModsAsync(cfg, store, paths, runtime, [presetMod]);
        });
        api.MapPost("/mods/install-batch", async (InstallBatchRequest req, RuntimeState runtime) =>
        {
            return await QueueWorkshopModsAsync(cfg, store, paths, runtime, req.Mods);
        });
        api.MapPost("/mods/preset", async (HttpRequest request) =>
        {
            var file = request.Form.Files["preset"];
            if (file is null) return Results.Json(new { error = "preset file required" }, statusCode: 400);
            var savedPath = await PresetFiles.SaveAsync(cfg, file);
            var html = await File.ReadAllTextAsync(savedPath);
            return Results.Json(new { mods = WithInstallStatus(cfg, PresetParser.Parse(html)), savedPath = PathGuard.Relative(cfg.Arma3Dir, savedPath) });
        });
        
        api.MapGet("/mods/preset-files", () => Results.Json(PresetFiles.List(cfg)));
        api.MapPost("/mods/preset-files/load", async (PresetFileLoadRequest req) =>
        {
            var file = PresetFiles.Resolve(cfg, req.Path);
            var html = await File.ReadAllTextAsync(file);
            return Results.Json(new { mods = WithInstallStatus(cfg, PresetParser.Parse(html)), savedPath = PathGuard.Relative(cfg.Arma3Dir, file) });
        });
        api.MapDelete("/mods/preset-files", (string path) =>
        {
            var file = PresetFiles.Resolve(cfg, path);
            File.Delete(file);
            return Results.Json(new { ok = true });
        });
        
        api.MapGet("/modlists", async () => Results.Json(await store.GetModlistsAsync()));
        api.MapPost("/modlists", async (ModlistSaveRequest req) => Results.Json(await store.SaveModlistAsync(req)));
        api.MapPut("/modlists/{id}/activate", async (string id, ServerLifecycleCoordinator lifecycle) =>
        {
            if (LifecycleOwnsServer(await lifecycle.GetStatusAsync())) return Results.Json(new { error = "Stop or cancel the game server before changing the active modlist" }, statusCode: 409);
            var state = await store.ActivateModlistAsync(id);
            var active = state.Lists.FirstOrDefault(list => list.Id == id);
            if (active is null) return Results.Json(new { error = "Modlist not found" }, statusCode: 404);
            var missing = WithInstallStatus(cfg, active.Mods).Where(mod => !mod.Installed).ToArray();
            return Results.Json(new { state.ActiveModlistId, state.Lists, missing });
        });
        api.MapPut("/modlists/{id}/deactivate", async (string id, ServerLifecycleCoordinator lifecycle) =>
        {
            if (LifecycleOwnsServer(await lifecycle.GetStatusAsync())) return Results.Json(new { error = "Stop or cancel the game server before changing the active modlist" }, statusCode: 409);
            var current = await store.GetModlistsAsync();
            if (current.Lists.All(list => list.Id != id)) return Results.Json(new { error = "Modlist not found" }, statusCode: 404);
            if (current.ActiveModlistId != id) return Results.Json(new { error = "Modlist is not active" }, statusCode: 409);
            return Results.Json(await store.DeactivateModlistAsync(id));
        });
        api.MapPost("/modlists/{id}/install-missing", async (string id, RuntimeState runtime) =>
        {
            var list = (await store.GetModlistsAsync()).Lists.FirstOrDefault(item => item.Id == id);
            return list is null
                ? Results.Json(new { error = "Modlist not found" }, statusCode: 404)
                : await QueueWorkshopModsAsync(cfg, store, paths, runtime, list.Mods);
        });
        api.MapDelete("/modlists/{id}", async (string id, bool? deleteMods) =>
        {
            var deleting = deleteMods == true ? await store.DeleteModsForModlistAsync(id) : [];
            foreach (var mod in deleting) await DeleteModFilesAndIndexAsync(cfg, store, mod);
            await store.DeleteModlistAsync(id);
            return Results.Json(new { ok = true, deletedMods = deleting.Count });
        });
        
        api.MapGet("/files", async (string? path, bool? refresh) =>
        {
            var dir = PathGuard.Resolve(cfg.Arma3Dir, path);
            var rel = PathGuard.Relative(cfg.Arma3Dir, dir);
            // Manual sync request: drop just this directory's index row so the read below falls back to a
            // single-level live listing instead of a full-tree rescan, keeping the CPU/RAM cost negligible.
            if (refresh == true) await store.InvalidateFileIndexDirAsync(rel);
            var indexed = WorkshopStorage.IsSymbolicLink(dir) ? null : await store.GetFileIndexChildrenAsync(rel);
            var items = indexed ?? await Task.Run(() => LiveListFallback(dir, cfg.Arma3Dir));
            if (rel.Length == 0)
            {
                var directorySizes = await store.GetRootDisplayDirectorySizesAsync();
                items = items.Select(item => item.IsDir && directorySizes.TryGetValue(item.Name, out var size)
                    ? item with { Size = size }
                    : item).ToArray();
            }
            return Results.Json(new { path = rel, rootName = "Arma 3 Server", items });
        });
        api.MapGet("/files/content", async (string path) =>
        {
            var file = PathGuard.Resolve(cfg.Arma3Dir, path);
            if (ProtectedFiles.IsProtected(PathGuard.Relative(cfg.Arma3Dir, file))) return Results.Json(new { error = "Protected file" }, statusCode: 403);
            if (Directory.Exists(file)) return Results.Json(new { error = "Path is a directory" }, statusCode: 400);
            var info = new FileInfo(file);
            if (info.Length > 2 * 1024 * 1024) return Results.Json(new { error = "File too large to edit (>2 MB)" }, statusCode: 400);
            return Results.Json(new { path = PathGuard.Relative(cfg.Arma3Dir, file), content = await File.ReadAllTextAsync(file) });
        });
        api.MapPut("/files/content", async (FileWriteRequest req) =>
        {
            var file = PathGuard.Resolve(cfg.Arma3Dir, req.Path);
            if (ProtectedFiles.IsProtected(PathGuard.Relative(cfg.Arma3Dir, file))) return Results.Json(new { error = "Protected file" }, statusCode: 403);
            await File.WriteAllTextAsync(file, req.Content);
            return Results.Json(new { ok = true });
        });
        api.MapPost("/files/upload", async (HttpRequest request, string? dir) =>
        {
            var target = PathGuard.Resolve(cfg.Arma3Dir, dir);
            if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) || string.IsNullOrWhiteSpace(mediaType.Boundary.Value))
                return Results.Json(new { error = "Expected multipart form data" }, statusCode: 400);
            var uploaded = await StreamUploadsAsync(request, target);
            // Invalidate just this directory's index row so the next listing falls back to a live read until
            // the watchdog's next cycle re-indexes it — keeps the upload visible immediately.
            await store.InvalidateFileIndexDirAsync(PathGuard.Relative(cfg.Arma3Dir, target));
            return Results.Json(new { ok = true, uploaded });
        });
        api.MapDelete("/files", async (string path) =>
        {
            var target = PathGuard.Resolve(cfg.Arma3Dir, path);
            if (Path.GetFullPath(target) == Path.GetFullPath(cfg.Arma3Dir))
                return Results.Json(new { error = "Cannot delete server root" }, statusCode: 400);
            if (ProtectedFiles.IsProtected(PathGuard.Relative(cfg.Arma3Dir, target))) return Results.Json(new { error = "Protected file" }, statusCode: 403);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            else if (File.Exists(target)) File.Delete(target);
            var removedMods = await store.RemoveModsForDeletedPathAsync(target);
            await store.RemoveFileIndexForDeletedPathAsync(PathGuard.Relative(cfg.Arma3Dir, target));
            return Results.Json(new { ok = true, removedMods });
        });

        api.MapPut("/files/rename", async (FileRenameRequest req) =>
        {
            var source = PathGuard.Resolve(cfg.Arma3Dir, req.Path);
            var newName = Path.GetFileName(req.NewName?.Trim() ?? "");
            if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/') || newName.Contains('\\') || newName == "." || newName == "..")
                return Results.Json(new { error = "Invalid name" }, statusCode: 400);
            if (ProtectedFiles.IsProtected(PathGuard.Relative(cfg.Arma3Dir, source)))
                return Results.Json(new { error = "Protected file" }, statusCode: 403);
            var parent = Path.GetDirectoryName(source) ?? cfg.Arma3Dir;
            var dest = Path.GetFullPath(Path.Combine(parent, newName));
            if (!dest.StartsWith(Path.GetFullPath(cfg.Arma3Dir), StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "Access denied" }, statusCode: 403);
            if (Path.Exists(dest) && !string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "A file or folder with that name already exists" }, statusCode: 409);
            if (Directory.Exists(source)) Directory.Move(source, dest);
            else File.Move(source, dest);
            var relNew = PathGuard.Relative(cfg.Arma3Dir, dest);
            await store.RemoveModsForDeletedPathAsync(source);
            await store.RemoveFileIndexForDeletedPathAsync(PathGuard.Relative(cfg.Arma3Dir, source));
            return Results.Json(new { ok = true, newPath = relNew, newName });
        });
        
        api.MapGet("/config", async (string? file) =>
        {
            file ??= "server.cfg";
            if (file is not ("server.cfg" or "basic.cfg")) return Results.Json(new { error = "Unknown config file" }, statusCode: 400);
            var full = Path.Combine(paths.ConfigDir, file);
            return Results.Json(new { file, content = await File.ReadAllTextAsync(full) });
        });
        api.MapPut("/config", async (ConfigWriteRequest req) =>
        {
            if (req.File is not ("server.cfg" or "basic.cfg")) return Results.Json(new { error = "Unknown config file" }, statusCode: 400);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigDir, req.File), req.Content);
            return Results.Json(new { ok = true });
        });
        api.MapGet("/logs", (RuntimeState runtime, int? limit) =>
            Results.Json(runtime.GetLogs(Math.Clamp(limit ?? 300, 0, LogHub.DefaultCapacity))));
        
        // Server-Sent Events — stable IDs support native Last-Event-ID reconnects while the
        // bounded LogHub reports an explicit gap if a client falls behind its retained history.
        api.MapGet("/logs/stream", (HttpContext http, LogStreamService stream, CancellationToken ct, long since = 0, string? batch = null) =>
        {
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            var lastId = Math.Max(0, since);
            if (long.TryParse(http.Request.Headers["Last-Event-ID"], out var reconnectId))
                lastId = Math.Max(lastId, reconnectId);
            var batched = batch == "1" || bool.TryParse(batch, out var enabled) && enabled;
            return TypedResults.ServerSentEvents(stream.Stream(lastId, ct, batched));
        });
        
        api.MapGet("/paths", () => Results.Json(paths));
        
        api.MapGet("/settings/account", async () =>
        {
            var auth = await store.GetPanelAuthAsync(cfg);
            return Results.Json(new { username = auth.Username });
        });
        
        api.MapPut("/settings/account", async (HttpContext http, AccountUpdateRequest req) =>
        {
            var current = await store.GetPanelAuthAsync(cfg);
            if (!PasswordHasher.Verify(req.CurrentPassword, current.PasswordSalt, current.PasswordHash))
                return Results.Json(new { error = "Current password is incorrect" }, statusCode: 400);
        
            var username = string.IsNullOrWhiteSpace(req.Username) ? current.Username : req.Username.Trim();
            var password = string.IsNullOrWhiteSpace(req.NewPassword) ? req.CurrentPassword : req.NewPassword;
            var updated = PasswordHasher.Create(username, password);
            await store.SavePanelAuthAsync(updated);
            http.Session.SetString("authenticated", "true");
            http.Session.SetString("authProof", SessionProof.Create(cfg.SessionSecret));
            return Results.Json(new { ok = true, username = updated.Username });
        });

        api.MapPost("/system/restart", async (RestartAppRequest req, RuntimeState runtime, ServerLifecycleCoordinator lifecycle, SteamCmdSession steam, IHostApplicationLifetime lifetime) =>
        {
            if (LifecycleOwnsServer(await lifecycle.GetStatusAsync()))
                return Results.Json(new { error = "Stop or cancel the game server before restarting the panel" }, statusCode: 409);
            if (runtime.IsMaintenanceBusy || steam.IsRunning)
                return Results.Json(new { error = "Wait for SteamCMD and maintenance tasks to finish" }, statusCode: 409);
            var auth = await store.GetPanelAuthAsync(cfg);
            if (!PasswordHasher.Verify(req.CurrentPassword, auth.PasswordSalt, auth.PasswordHash))
                return Results.Json(new { error = "Current password is incorrect" }, statusCode: 400);

            // Exits the process only; podman's `restart: unless-stopped` policy brings the container straight
            // back up. No volumes are touched, so mods, config, and SQLite state survive untouched.
            _ = Task.Run(async () =>
            {
                await Task.Delay(750);
                lifetime.StopApplication();
            });
            return Results.Json(new { accepted = true, message = "Panel is restarting. This takes a few seconds." }, statusCode: StatusCodes.Status202Accepted);
        });

        api.MapPost("/system/factory-reset", async (FactoryResetRequest req, RuntimeState runtime, ServerLifecycleCoordinator lifecycle, SteamCmdSession steam, IHostApplicationLifetime lifetime) =>
        {
            if (LifecycleOwnsServer(await lifecycle.GetStatusAsync()))
                return Results.Json(new { error = "Stop or cancel the game server before factory reset" }, statusCode: 409);
            if (runtime.IsMaintenanceBusy || steam.IsRunning)
                return Results.Json(new { error = "Wait for SteamCMD and maintenance tasks to finish" }, statusCode: 409);
            var auth = await store.GetPanelAuthAsync(cfg);
            if (!PasswordHasher.Verify(req.CurrentPassword, auth.PasswordSalt, auth.PasswordHash))
                return Results.Json(new { error = "Current password is incorrect" }, statusCode: 400);
            if (!string.Equals(req.Confirmation, FactoryResetExecutor.Confirmation, StringComparison.Ordinal))
                return Results.Json(new { error = $"Confirmation must be exactly: {FactoryResetExecutor.Confirmation}" }, statusCode: 400);

            await FactoryResetExecutor.PrepareAsync(cfg.Arma3Dir);
            _ = Task.Run(async () =>
            {
                await Task.Delay(750);
                lifetime.StopApplication();
            });
            return Results.Json(new
            {
                accepted = true,
                message = "Factory reset queued. The API will restart, erase all persistent volume contents, and initialize clean storage."
            }, statusCode: StatusCodes.Status202Accepted);
        });
        
        api.MapPost("/steamcmd/factory-reset", (RuntimeState runtime) =>
        {
            SteamCmdSession.ResetCache();
            if (!runtime.RunTask(paths.SteamCmd, ["+quit"], "steamcmd-factory-reset"))
                return Results.Json(new { error = "SteamCMD factory reset is already running" }, statusCode: 409);
            return Results.Json(new { ok = true, message = "SteamCMD reset started" });
        });
        
        api.MapGet("/steamcmd/login", async (SteamCmdSession steam) => Results.Json(await steam.PublicStateAsync(true)));
        api.MapPost("/steamcmd/login/start", async (SteamLoginRequest req, SteamCmdSession steam) =>
        {
            await steam.StartAsync(req.Username, req.Password);
            await store.SaveSteamAuthAsync(req.Username);
            return Results.Json(await steam.PublicStateAsync(true));
        });
        api.MapPost("/steamcmd/login/input", async (SteamInputRequest req, SteamCmdSession steam) =>
        {
            steam.Write(req.Input);
            return Results.Json(new { ok = true });
        });
        api.MapPost("/steamcmd/login/cancel", (SteamCmdSession steam) =>
        {
            steam.Cancel();
            return Results.Json(new { ok = true });
        });
        
    }

    // Read-only listing used only when a directory hasn't been indexed yet (startup race, or a just-invalidated
    // upload target). Never writes to file_index — the watchdog is the sole writer to avoid write contention.
    static FileItem[] LiveListFallback(string dir, string arma3Dir) =>
        Directory.EnumerateFileSystemEntries(dir).Select(p =>
        {
            var info = new FileInfo(p);
            var isDir = Directory.Exists(p);
            return new FileItem(Path.GetFileName(p), PathGuard.Relative(arma3Dir, p), isDir, isDir ? 0 : info.Length, info.LastWriteTimeUtc);
        }).Where(i => !ProtectedFiles.IsProtected(i.Path)).OrderByDescending(i => i.IsDir).ThenBy(i => i.Name).ToArray();

    static bool IsAuthed(HttpContext http, AppConfig cfg) =>
        http.Session.GetString("authenticated") == "true" &&
        SessionProof.Verify(cfg.SessionSecret, http.Session.GetString("authProof"));

    static string LogicalOperator(HttpContext http)
    {
        var steamId = http.Session.GetString("steamId");
        return !string.IsNullOrWhiteSpace(steamId)
            ? $"steam:{steamId}"
            : http.Session.GetString("authMethod") ?? "panel";
    }

    static bool LifecycleOwnsServer(ServerLifecycleStatus status) => status.Phase is not ("stopped" or "faulted");
    
    static async ValueTask<object?> AuthFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var cfg = http.RequestServices.GetRequiredService<AppConfig>();
        if (!IsAuthed(http, cfg)) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
        return await next(ctx);
    }
    
    static string BaseUrl(HttpContext http, AppConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.BaseUrl)) return cfg.BaseUrl;
        var proto = http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? http.Request.Scheme;
        var host = http.Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? http.Request.Host.ToString();
        return $"{proto}://{host}";
    }
    
    static string? ResolvedSteamUser(AppConfig cfg, SteamAuth? auth) =>
        !string.IsNullOrWhiteSpace(cfg.SteamUser) && cfg.SteamUser != "anonymous" ? cfg.SteamUser : auth?.Username;
    
    static IResult SteamLoginRequired(string? username) => Results.Json(new
    {
        error = "SteamCMD login required before installing server files or mods.",
        code = "steam_login_required",
        username
    }, statusCode: 409);
    
    static string[] SteamArgs(AppConfig cfg, SteamAuth? auth, params string[] tail)
    {
        var user = ResolvedSteamUser(cfg, auth);
        if (string.IsNullOrWhiteSpace(user)) throw new InvalidOperationException("Steam credentials required");
        var args = new List<string> { "+login", user };
        if (!string.IsNullOrWhiteSpace(cfg.SteamPass)) args.Add(cfg.SteamPass);
        args.AddRange(tail);
        return args.ToArray();
    }
    
    static string[] ServerUpdateArgs(AppConfig cfg, StartupSettings startup)
    {
        var args = new List<string> { "+app_update", "233780" };
        args.Add("validate");
        args.AddRange(CommandBuilder.SplitArgs(startup.SteamCmdFlags));
        return args.ToArray();
    }
    
    static string[] CreatorDlcDownloadArgs(AppConfig cfg)
    {
        var args = new List<string> { "+app_update", "233780", "-beta", "creatordlc", "validate" };
        foreach (var appId in cfg.CreatorDlcAppIds)
        {
            args.AddRange(["+app_update", appId, "validate"]);
        }
        return args.ToArray();
    }

    static List<PresetMod> WithInstallStatus(AppConfig cfg, IEnumerable<PresetMod> mods) => mods
        .Where(mod => Regex.IsMatch(mod.WorkshopId ?? "", @"^\d+$"))
        .GroupBy(mod => mod.WorkshopId)
        .Select(group => group.First() with { Installed = WorkshopStorage.IsInstalled(cfg, group.Key) })
        .ToList();

    static async Task<IResult> QueueWorkshopModsAsync(AppConfig cfg, SqliteStore store, ServerPaths paths, RuntimeState runtime, IEnumerable<PresetMod> requested)
    {
        var mods = WithInstallStatus(cfg, requested);
        if (mods.Count == 0) return Results.Json(new { error = "No valid Workshop IDs" }, statusCode: 400);

        var installed = mods.Where(mod => mod.Installed).ToList();
        if (installed.Count > 0) await FinalizeWorkshopModsAsync(cfg, store, installed);
        var missing = mods.Where(mod => !mod.Installed).ToList();
        if (missing.Count == 0) return Results.Json(new { ok = true, queued = 0, alreadyInstalled = installed.Count });

        var auth = await store.GetSteamAuthAsync();
        var user = ResolvedSteamUser(cfg, auth);
        if (!SteamCmdSession.HasCachedLogin(user)) return SteamLoginRequired(user);
        _ = Task.Run(async () =>
        {
            try
            {
                var failed = await DownloadWorkshopModsWithRetriesAsync(cfg, store, paths, runtime, auth, missing, "mods:batch");
                if (failed.Count > 0) runtime.Push("stderr", $"Workshop mods failed after retries: {string.Join(", ", failed.Select(mod => mod.WorkshopId))}");
            }
            catch (Exception exception) { runtime.Push("stderr", $"Workshop download failed: {exception.Message}"); }
        });
        return Results.Json(new { ok = true, queued = missing.Count, alreadyInstalled = installed.Count });
    }
    
    static async Task<int> FinalizeWorkshopModsAsync(AppConfig cfg, SqliteStore store, IEnumerable<PresetMod> mods)
    {
        var completed = 0;
        foreach (var mod in mods
            .Where(m => Regex.IsMatch(m.WorkshopId ?? "", @"^\d+$"))
            .GroupBy(m => m.WorkshopId)
            .Select(g => g.First()))
        {
            var source = WorkshopStorage.Source(cfg, mod.WorkshopId);
            if (!Directory.Exists(source)) continue;
            var path = WorkshopStorage.EnsureReference(cfg, mod.WorkshopId);
            await store.UpsertModAsync(new Mod(Guid.NewGuid().ToString("n"), string.IsNullOrWhiteSpace(mod.Name) ? $"@{mod.WorkshopId}" : mod.Name, path, true, mod.WorkshopId));
            completed++;
        }
        WorkshopStorage.RepairDuplicates(cfg);
        await store.NormalizeWorkshopModPathsAsync(cfg);
        if (completed > 0)
        {
            // One completion event per batch: force only affected File Manager listings to read live once.
            await store.InvalidateFileIndexDirAsync("");
            await store.InvalidateFileIndexDirAsync("steamapps/workshop/content/107410");
        }
        return completed;
    }

    static async Task DeleteModFilesAndIndexAsync(AppConfig cfg, SqliteStore store, Mod mod)
    {
        if (!string.IsNullOrWhiteSpace(mod.WorkshopId))
        {
            var reference = WorkshopStorage.Reference(cfg, mod.WorkshopId);
            var source = WorkshopStorage.Source(cfg, mod.WorkshopId);
            WorkshopStorage.Delete(cfg, mod.WorkshopId);
            await store.RemoveFileIndexForDeletedPathAsync(PathGuard.Relative(cfg.Arma3Dir, reference));
            await store.RemoveFileIndexForDeletedPathAsync(PathGuard.Relative(cfg.Arma3Dir, source));
            return;
        }

        if (string.IsNullOrWhiteSpace(mod.Path)) return;
        if (Directory.Exists(mod.Path)) Directory.Delete(mod.Path, true);
        await store.RemoveFileIndexForDeletedPathAsync(PathGuard.Relative(cfg.Arma3Dir, mod.Path));
    }

    static async Task<List<PresetMod>> DownloadWorkshopModsWithRetriesAsync(
        AppConfig cfg, SqliteStore store, ServerPaths paths, RuntimeState runtime, SteamAuth? auth,
        IReadOnlyCollection<PresetMod> requested, string kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mods = requested.GroupBy(mod => mod.WorkshopId).Select(group => group.First()).ToList();
        var initial = await runtime.RunTaskCaptureAsync(paths.SteamCmd, WorkshopDownloadArgs(cfg, auth, mods), $"{kind}:{mods.Count}");
        cancellationToken.ThrowIfCancellationRequested();
        var failed = FailedWorkshopMods(cfg, mods, initial);
        await FinalizeWorkshopModsAsync(cfg, store, mods.Where(mod => failed.All(item => item.WorkshopId != mod.WorkshopId)));

        var remaining = new List<PresetMod>();
        foreach (var mod in failed)
        {
            var downloaded = false;
            var delays = new[] { 2, 5, 15 };
            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                runtime.Push("system", $"Workshop {mod.WorkshopId} retry {attempt + 1}/{delays.Length} in {delays[attempt]}s");
                await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), cancellationToken);
                var result = await runtime.RunTaskCaptureAsync(paths.SteamCmd, WorkshopDownloadArgs(cfg, auth, [mod]), $"{kind}:retry:{mod.WorkshopId}:{attempt + 1}");
                cancellationToken.ThrowIfCancellationRequested();
                if (FailedWorkshopMods(cfg, [mod], result).Count != 0) continue;
                await FinalizeWorkshopModsAsync(cfg, store, [mod]);
                runtime.Push("system", $"Workshop {mod.WorkshopId} downloaded successfully after retry {attempt + 1}");
                downloaded = true;
                break;
            }
            if (!downloaded) remaining.Add(mod);
        }
        return remaining;
    }

    static string[] WorkshopDownloadArgs(AppConfig cfg, SteamAuth? auth, IEnumerable<PresetMod> mods)
    {
        var args = new List<string> { "+force_install_dir", cfg.Arma3Dir };
        args.AddRange(SteamArgs(cfg, auth));
        foreach (var mod in mods) args.AddRange(["+workshop_download_item", "107410", mod.WorkshopId, "validate"]);
        args.Add("+quit");
        return args.ToArray();
    }

    static List<PresetMod> FailedWorkshopMods(AppConfig cfg, IEnumerable<PresetMod> mods, TaskRunResult result)
    {
        var text = string.Join('\n', result.Output);
        var failedIds = Regex.Matches(text, @"Download item\s+(\d+)\s+failed", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var globalFailure = result.ExitCode != 0 && failedIds.Count == 0;
        return mods.Where(mod => globalFailure || failedIds.Contains(mod.WorkshopId) || !Directory.Exists(WorkshopStorage.Source(cfg, mod.WorkshopId))).ToList();
    }

    static async Task<List<string>> StreamUploadsAsync(HttpRequest request, string target)
    {
        var mediaType = MediaTypeHeaderValue.Parse(request.ContentType!);
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value!;
        var reader = new MultipartReader(boundary, request.Body);
        var uploaded = new List<string>();
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(request.HttpContext.RequestAborted)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.IsFileDisposition()) continue;
            var encodedName = disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName;
            var name = Path.GetFileName(HeaderUtilities.RemoveQuotes(encodedName).Value);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (ProtectedFiles.IsProtected(name)) throw new BadHttpRequestException("Protected file", StatusCodes.Status403Forbidden);

            var destination = Path.Combine(target, name);
            var temporary = Path.Combine(target, $".a3mgr-upload-{Guid.NewGuid():N}");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await section.Body.CopyToAsync(output, request.HttpContext.RequestAborted);
                File.Move(temporary, destination, true);
                uploaded.Add(name);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return uploaded;
    }

    // Fast checks (binary present, Steam login cached) run synchronously so bad requests still fail fast.
    // The slow part — Workshop mod update/download, which can run for minutes — moves to a background task
    // so the HTTP request returns immediately instead of blocking until it's done. Previously the whole
    // sequence ran inline, and browsers/reverse proxies would drop the connection (499) long before it
    // finished, making the panel look hung even though the start was still progressing server-side.
    static async Task<IResult> StartServerAsync(
        AppConfig cfg,
        ServerPaths paths,
        SqliteStore store,
        RuntimeState runtime,
        ServerLifecycleCoordinator lifecycle,
        PlayerActivityService activity,
        bool restart = false)
    {
        var startup = await store.GetStartupAsync(paths, cfg);
        var bin = Path.Combine(cfg.Arma3Dir, startup.ServerBinary);
        if (!cfg.MockServer && !File.Exists(bin))
            return Results.Json(new { error = "Server binary not found. Please install the server first." }, statusCode: 400);

        var workshopMods = await store.GetActiveWorkshopModsAsync();
        SteamAuth? auth = null;
        if (workshopMods.Count > 0)
        {
            auth = await store.GetSteamAuthAsync();
            var user = ResolvedSteamUser(cfg, auth);
            if (!SteamCmdSession.HasCachedLogin(user)) return SteamLoginRequired(user);
        }

        var reservation = restart
            ? await lifecycle.TryBeginRestartAsync()
            : await lifecycle.TryBeginStartAsync();
        if (!reservation.Accepted || reservation.OperationId is null)
            return Results.Json(new
            {
                error = restart ? "Server cannot be restarted in its current state" : "A server start or active session already exists",
                code = "server_lifecycle_conflict",
                status = reservation.Status
            }, statusCode: 409);

        var operationId = reservation.OperationId;
        lifecycle.QueueStart(operationId, async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (workshopMods.Count > 0)
            {
                if (!await lifecycle.SetStartStageAsync(operationId, "preparing", "updating_mods"))
                    throw new OperationCanceledException(cancellationToken);
                runtime.Push("system", $"Checking updates for {workshopMods.Count} active Workshop mods before server start");
                var checking = workshopMods.Select(mod => new PresetMod(mod.Name, mod.WorkshopId!)).ToList();
                var failed = await DownloadWorkshopModsWithRetriesAsync(cfg, store, paths, runtime, auth, checking, "mods:startup-check", cancellationToken);
                if (failed.Count > 0)
                {
                    throw new InvalidOperationException($"SteamCMD could not update {string.Join(", ", failed.Select(mod => mod.WorkshopId))}. Review Server Logs and try again.");
                }
                runtime.Push("system", "Active Workshop mods are current and storage is optimized");
            }
            else
            {
                WorkshopStorage.RepairDuplicates(cfg);
                await store.NormalizeWorkshopModPathsAsync(cfg);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await lifecycle.SetStartStageAsync(operationId, "preparing", "configuring"))
                throw new OperationCanceledException(cancellationToken);
            var mods = (await store.GetActiveCreatorDlcPathsAsync(cfg)).Concat(await store.GetActiveModsAsync()).ToList();
            var lowercased = ModFileRepair.MakeLowercase(mods);
            if (lowercased > 0) runtime.Push("system", $"Repaired {lowercased} uppercase mod file/folder names before start");
            await ServerCfgWriter.ApplyAsync(startup);
            await BattlEyeConfigWriter.ApplyAsync(startup, cfg);
            var instrumentation = await ServerCfgInstrumentation.ApplyAsync(startup.ServerCfg);
            activity.ReportInstrumentation(instrumentation);
            activity.ConfigureForRun(!startup.DisableBattleEye);
            if (!instrumentation.Complete)
                runtime.Push("stderr", $"Player tracking is partial: {string.Join("; ", instrumentation.Errors)}");
            cancellationToken.ThrowIfCancellationRequested();
            await lifecycle.LaunchAsync(operationId, bin, CommandBuilder.Args(paths, startup, mods), cfg.Arma3Dir, startup.ProfilesDir);

            if (startup.HeadlessClients > 0)
            {
                await Task.Delay(3000, cancellationToken);
                for (var i = 1; i <= startup.HeadlessClients; i++)
                {
                    var hcProfileDir = Path.Combine(startup.ProfilesDir, $"hc{i}");
                    Directory.CreateDirectory(hcProfileDir);
                    lifecycle.StartHeadlessClient(bin, CommandBuilder.HeadlessClientArgs(paths, startup, mods, i, hcProfileDir), cfg.Arma3Dir, i);
                }
            }
        });

        return Results.Json(new { accepted = true, checkedMods = workshopMods.Count, status = reservation.Status }, statusCode: StatusCodes.Status202Accepted);
    }
}
