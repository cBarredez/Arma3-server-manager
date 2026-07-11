using System.Net.Http.Headers;
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
        
        api.MapGet("/server/status", async (RuntimeState runtime) =>
        {
            var startup = await store.GetStartupAsync(paths, cfg);
            var joinHost = !string.IsNullOrWhiteSpace(cfg.PublicJoinHost)
                ? cfg.PublicJoinHost
                : Uri.TryCreate(cfg.BaseUrl, UriKind.Absolute, out var baseUri) ? baseUri.Host : "";
            return Results.Json(new { running = runtime.IsRunning, pid = runtime.ProcessId, port = startup.Port, joinHost });
        });
        
        api.MapPost("/server/start", async (RuntimeState runtime) =>
        {
            if (runtime.IsRunning) return Results.Json(new { error = "Server is already running" }, statusCode: 400);
            var startup = await store.GetStartupAsync(paths, cfg);
            var bin = Path.Combine(cfg.Arma3Dir, startup.ServerBinary);
            if (!cfg.MockServer && !File.Exists(bin))
                return Results.Json(new { error = "Server binary not found. Please install the server first." }, statusCode: 400);
            var mods = (await store.GetActiveCreatorDlcPathsAsync(cfg)).Concat(await store.GetActiveModsAsync()).ToList();
            var lowercased = ModFileRepair.MakeLowercase(mods);
            if (lowercased > 0) runtime.Push("system", $"Repaired {lowercased} uppercase mod file/folder names before start");
            await BattlEyeConfigWriter.ApplyAsync(paths, cfg);
            runtime.Start(bin, CommandBuilder.Args(paths, startup, mods), cfg.Arma3Dir);
            return Results.Json(new { ok = true, pid = runtime.ProcessId, lowercased });
        });

        api.MapPost("/server/stop", async (RuntimeState runtime, BattlEyeRconClient rcon) =>
        {
            if (!runtime.IsRunning) return Results.Json(new { error = "Server is not running" }, statusCode: 400);
            runtime.Stop();
            await rcon.DisconnectAsync();
            return Results.Json(new { ok = true });
        });

        api.MapPost("/server/restart", async (RuntimeState runtime, BattlEyeRconClient rcon) =>
        {
            if (runtime.IsRunning) runtime.Stop();
            await rcon.DisconnectAsync();
            await Task.Delay(1000);
            var startup = await store.GetStartupAsync(paths, cfg);
            var mods = (await store.GetActiveCreatorDlcPathsAsync(cfg)).Concat(await store.GetActiveModsAsync()).ToList();
            var lowercased = ModFileRepair.MakeLowercase(mods);
            if (lowercased > 0) runtime.Push("system", $"Repaired {lowercased} uppercase mod file/folder names before restart");
            await BattlEyeConfigWriter.ApplyAsync(paths, cfg);
            runtime.Start(Path.Combine(cfg.Arma3Dir, startup.ServerBinary), CommandBuilder.Args(paths, startup, mods), cfg.Arma3Dir);
            return Results.Json(new { ok = true, pid = runtime.ProcessId, lowercased });
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

        api.MapPost("/server/rcon/kick", async (RconKickRequest req, RuntimeState runtime, BattlEyeRconClient rcon) =>
        {
            if (!runtime.IsRunning) return Results.Json(new { error = "Server is not running" }, statusCode: 400);
            try { return Results.Json(new { ok = true, response = await rcon.KickAsync(req.PlayerId, req.Reason) }); }
            catch (Exception exception) { return Results.Json(new { error = exception.Message }, statusCode: 502); }
        });

        api.MapPost("/server/rcon/ban", async (RconBanRequest req, RuntimeState runtime, BattlEyeRconClient rcon) =>
        {
            if (!runtime.IsRunning) return Results.Json(new { error = "Server is not running" }, statusCode: 400);
            try { return Results.Json(new { ok = true, response = await rcon.BanAsync(req.PlayerId, req.Minutes, req.Reason) }); }
            catch (Exception exception) { return Results.Json(new { error = exception.Message }, statusCode: 502); }
        });

        api.MapPost("/server/install", async (RuntimeState runtime) =>
        {
            var auth = await store.GetSteamAuthAsync();
            var user = ResolvedSteamUser(cfg, auth);
            if (!SteamCmdSession.HasCachedLogin(user)) return SteamLoginRequired(user);
            var startup = await store.GetStartupAsync(paths, cfg);
            var args = SteamArgs(cfg, auth, ServerUpdateArgs(cfg, startup));
            runtime.RunTask(paths.SteamCmd, ["+force_install_dir", cfg.Arma3Dir, .. args, "+quit"], "install");
            return Results.Json(new { ok = true, message = "Server installation started" });
        });
        
        api.MapPost("/server/update", async (RuntimeState runtime) =>
        {
            var auth = await store.GetSteamAuthAsync();
            var user = ResolvedSteamUser(cfg, auth);
            if (!SteamCmdSession.HasCachedLogin(user)) return SteamLoginRequired(user);
            var startup = await store.GetStartupAsync(paths, cfg);
            var args = SteamArgs(cfg, auth, ServerUpdateArgs(cfg, startup));
            runtime.RunTask(paths.SteamCmd, ["+force_install_dir", cfg.Arma3Dir, .. args, "+quit"], "update");
            return Results.Json(new { ok = true, message = "Update started" });
        });
        
        api.MapPost("/server/download-creator-dlcs", async (RuntimeState runtime) =>
        {
            var auth = await store.GetSteamAuthAsync();
            var user = ResolvedSteamUser(cfg, auth);
            if (!SteamCmdSession.HasCachedLogin(user)) return SteamLoginRequired(user);
            var args = SteamArgs(cfg, auth, CreatorDlcDownloadArgs(cfg));
            runtime.RunTask(paths.SteamCmd, ["+force_install_dir", cfg.Arma3Dir, .. args, "+quit"], "creator-dlcs");
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
            if (!string.IsNullOrWhiteSpace(mod.Path) && Directory.Exists(mod.Path)) Directory.Delete(mod.Path, true);
            return Results.Json(new { ok = true });
        });
        api.MapPost("/mods/install", async (InstallModRequest req, RuntimeState runtime) =>
        {
            var auth = await store.GetSteamAuthAsync();
            var user = ResolvedSteamUser(cfg, auth);
            if (!SteamCmdSession.HasCachedLogin(user)) return SteamLoginRequired(user);
            var presetMod = new PresetMod(req.Name ?? $"@{req.WorkshopId}", req.WorkshopId);
            var args = SteamArgs(cfg, auth, "+workshop_download_item", "107410", req.WorkshopId, "validate");
            runtime.RunTask(paths.SteamCmd, ["+force_install_dir", cfg.Arma3Dir, .. args, "+quit"], $"mod:{req.WorkshopId}", async _ => await FinalizeWorkshopModsAsync(cfg, store, [presetMod]));
            return Results.Json(new { ok = true, queued = 1 });
        });
        api.MapPost("/mods/install-batch", async (InstallBatchRequest req, RuntimeState runtime) =>
        {
            var auth = await store.GetSteamAuthAsync();
            var user = ResolvedSteamUser(cfg, auth);
            if (!SteamCmdSession.HasCachedLogin(user)) return SteamLoginRequired(user);
            var mods = req.Mods
                .Where(m => Regex.IsMatch(m.WorkshopId ?? "", @"^\d+$"))
                .GroupBy(m => m.WorkshopId)
                .Select(g => g.First())
                .ToList();
            if (mods.Count == 0) return Results.Json(new { error = "No valid Workshop IDs" }, statusCode: 400);
            var taskArgs = new List<string> { "+force_install_dir", cfg.Arma3Dir };
            taskArgs.AddRange(SteamArgs(cfg, auth));
            foreach (var mod in mods)
            {
                taskArgs.AddRange(["+workshop_download_item", "107410", mod.WorkshopId, "validate"]);
            }
            taskArgs.Add("+quit");
            runtime.RunTask(paths.SteamCmd, taskArgs, $"mods:batch:{mods.Count}", async _ => await FinalizeWorkshopModsAsync(cfg, store, mods));
            return Results.Json(new { ok = true, queued = mods.Count });
        });
        api.MapPost("/mods/preset", async (HttpRequest request) =>
        {
            var file = request.Form.Files["preset"];
            if (file is null) return Results.Json(new { error = "preset file required" }, statusCode: 400);
            var savedPath = await PresetFiles.SaveAsync(cfg, file);
            var html = await File.ReadAllTextAsync(savedPath);
            return Results.Json(new { mods = PresetParser.Parse(html), savedPath = PathGuard.Relative(cfg.Arma3Dir, savedPath) });
        });
        
        api.MapGet("/mods/preset-files", () => Results.Json(PresetFiles.List(cfg)));
        api.MapPost("/mods/preset-files/load", async (PresetFileLoadRequest req) =>
        {
            var file = PresetFiles.Resolve(cfg, req.Path);
            var html = await File.ReadAllTextAsync(file);
            return Results.Json(new { mods = PresetParser.Parse(html), savedPath = PathGuard.Relative(cfg.Arma3Dir, file) });
        });
        api.MapDelete("/mods/preset-files", (string path) =>
        {
            var file = PresetFiles.Resolve(cfg, path);
            File.Delete(file);
            return Results.Json(new { ok = true });
        });
        
        api.MapGet("/modlists", async () => Results.Json(await store.GetModlistsAsync()));
        api.MapPost("/modlists", async (ModlistSaveRequest req) => Results.Json(await store.SaveModlistAsync(req)));
        api.MapPut("/modlists/{id}/activate", async (string id) => Results.Json(await store.ActivateModlistAsync(id)));
        api.MapPost("/modlists/{id}/install-missing", async (string id) => Results.Json(new { ok = true, queued = 0, id }));
        api.MapDelete("/modlists/{id}", async (string id, bool? deleteMods) =>
        {
            var deletedMods = deleteMods == true ? await store.DeleteModsForModlistAsync(id) : 0;
            await store.DeleteModlistAsync(id);
            return Results.Json(new { ok = true, deletedMods });
        });
        
        api.MapGet("/files", async (string? path) =>
        {
            var dir = PathGuard.Resolve(cfg.Arma3Dir, path);
            var rel = PathGuard.Relative(cfg.Arma3Dir, dir);
            var items = await store.GetFileIndexChildrenAsync(rel) ?? await Task.Run(() => LiveListFallback(dir, cfg.Arma3Dir));
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
            foreach (var file in request.Form.Files)
            {
                if (ProtectedFiles.IsProtected(file.FileName)) return Results.Json(new { error = "Protected file" }, statusCode: 403);
                await using var output = File.Create(Path.Combine(target, Path.GetFileName(file.FileName)));
                await file.CopyToAsync(output);
            }
            // Invalidate just this directory's index row so the next listing falls back to a live read until
            // the watchdog's next cycle re-indexes it — keeps the upload visible immediately.
            await store.InvalidateFileIndexDirAsync(PathGuard.Relative(cfg.Arma3Dir, target));
            return Results.Json(new { ok = true, uploaded = request.Form.Files.Select(f => f.FileName).ToArray() });
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
        api.MapGet("/logs", (RuntimeState runtime, int? limit) => Results.Json(runtime.Logs.TakeLast(Math.Min(limit ?? 300, 1000))));
        
        // Server-Sent Events — pushes new log lines and status changes in real time.
        // The client sends ?since=N where N is the last log index it received.
        // Reconnects are handled automatically by the browser EventSource API.
        api.MapGet("/logs/stream", async (HttpContext http, RuntimeState runtime, CancellationToken ct, int since = 0) =>
        {
            http.Response.Headers.ContentType  = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection   = "keep-alive";
            // Disable response buffering so bytes reach the client immediately
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await http.Response.Body.FlushAsync(ct);
        
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var lastIndex = Math.Max(0, since);
            var wasRunning  = runtime.IsRunning;
        
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var logs = runtime.Logs;
        
                    // Send any new log lines
                    if (logs.Count > lastIndex)
                    {
                        for (var i = lastIndex; i < logs.Count; i++)
                        {
                            var line = $"data: {JsonSerializer.Serialize(logs[i], json)}\n\n";
                            await http.Response.WriteAsync(line, ct);
                        }
                        await http.Response.Body.FlushAsync(ct);
                        lastIndex = logs.Count;
                    }
        
                    // Send status event when running state changes
                    var isRunning = runtime.IsRunning;
                    if (isRunning != wasRunning)
                    {
                        var evt = $"event: status\ndata: {JsonSerializer.Serialize(new { running = isRunning, pid = runtime.ProcessId }, json)}\n\n";
                        await http.Response.WriteAsync(evt, ct);
                        await http.Response.Body.FlushAsync(ct);
                        wasRunning = isRunning;
                    }
        
                    // Keep-alive ping every 15 s so proxies don't close the connection
                    if (lastIndex % 30 == 0)
                    {
                        await http.Response.WriteAsync(": ping\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                    }
        
                    await Task.Delay(300, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
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
        
        api.MapPost("/steamcmd/factory-reset", (RuntimeState runtime) =>
        {
            SteamCmdSession.ResetCache();
            runtime.RunTask(paths.SteamCmd, ["+quit"], "steamcmd-factory-reset");
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
    
    static async Task<int> FinalizeWorkshopModsAsync(AppConfig cfg, SqliteStore store, IEnumerable<PresetMod> mods)
    {
        var completed = 0;
        foreach (var mod in mods
            .Where(m => Regex.IsMatch(m.WorkshopId ?? "", @"^\d+$"))
            .GroupBy(m => m.WorkshopId)
            .Select(g => g.First()))
        {
            var source = Path.Combine(cfg.Arma3Dir, "steamapps", "workshop", "content", "107410", mod.WorkshopId);
            if (!Directory.Exists(source)) continue;
    
            var target = Path.Combine(cfg.Arma3Dir, $"@{mod.WorkshopId}");
            if (!Directory.Exists(target))
            {
                try
                {
                    Directory.CreateSymbolicLink(target, source);
                }
                catch
                {
                    CopyDirectory(source, target);
                }
            }
    
            await store.UpsertModAsync(new Mod(Guid.NewGuid().ToString("n"), string.IsNullOrWhiteSpace(mod.Name) ? $"@{mod.WorkshopId}" : mod.Name, target, true, mod.WorkshopId));
            completed++;
        }
        return completed;
    }
    
    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}
