using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var cfg = AppConfig.FromEnvironment();
var paths = await ServerPaths.DetectAsync(cfg);
var store = new SqliteStore(Path.Combine(cfg.Arma3Dir, "manager.sqlite3"));
await store.InitAsync();
await store.MigrateJsonStateAsync(paths);

builder.WebHost.UseKestrel(options => options.ListenAnyIP(cfg.WebPort));
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<RuntimeState>();
builder.Services.AddSingleton<SteamCmdSession>();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "a3mgr.sid";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.IdleTimeout = TimeSpan.FromHours(24);
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 500L * 1024 * 1024);
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        if (string.IsNullOrWhiteSpace(cfg.FrontendOrigin))
            policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials();
        else
            policy.WithOrigins(cfg.FrontendOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    });
});

var app = builder.Build();
app.UseCors("frontend");
app.UseSession();

app.MapGet("/api/health", () => Results.Json(new { status = "ok", backend = "dotnet-kestrel", sqlite = store.DbPath }));

app.MapPost("/api/auth/login", async (HttpContext http, LoginRequest req) =>
{
    var auth = await store.GetPanelAuthAsync(cfg);
    if (req.Username == auth.Username && PasswordHasher.Verify(req.Password, auth.PasswordSalt, auth.PasswordHash))
    {
        http.Session.SetString("authenticated", "true");
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
    authenticated = IsAuthed(http),
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
    if (Environment.GetEnvironmentVariable("MOCK_SERVER") != "true" && !File.Exists(bin))
        return Results.Json(new { error = "Server binary not found. Please install the server first." }, statusCode: 400);
    var mods = (await store.GetActiveCreatorDlcPathsAsync(cfg)).Concat(await store.GetActiveModsAsync()).ToList();
    var lowercased = ModFileRepair.MakeLowercase(mods);
    if (lowercased > 0) runtime.Push("system", $"Repaired {lowercased} uppercase mod file/folder names before start");
    runtime.Start(bin, CommandBuilder.Args(paths, startup, mods), cfg.Arma3Dir);
    return Results.Json(new { ok = true, pid = runtime.ProcessId, lowercased });
});

api.MapPost("/server/stop", (RuntimeState runtime) =>
{
    if (!runtime.IsRunning) return Results.Json(new { error = "Server is not running" }, statusCode: 400);
    runtime.Stop();
    return Results.Json(new { ok = true });
});

api.MapPost("/server/restart", async (RuntimeState runtime) =>
{
    if (runtime.IsRunning) runtime.Stop();
    await Task.Delay(1000);
    var startup = await store.GetStartupAsync(paths, cfg);
    var mods = (await store.GetActiveCreatorDlcPathsAsync(cfg)).Concat(await store.GetActiveModsAsync()).ToList();
    var lowercased = ModFileRepair.MakeLowercase(mods);
    if (lowercased > 0) runtime.Push("system", $"Repaired {lowercased} uppercase mod file/folder names before restart");
    runtime.Start(Path.Combine(cfg.Arma3Dir, startup.ServerBinary), CommandBuilder.Args(paths, startup, mods), cfg.Arma3Dir);
    return Results.Json(new { ok = true, pid = runtime.ProcessId, lowercased });
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

api.MapGet("/metrics", () =>
{
    var memory = MetricsReader.ReadMemory();
    return Results.Json(new
    {
        cpu = new { load = 0, cores = Array.Empty<int>() },
        memory,
        disk = new[] { MetricsReader.ReadDisk(paths.Arma3Dir, "/arma3") },
        temperature = (int?)null,
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
    var items = await Task.Run(() => Directory.EnumerateFileSystemEntries(dir).Select(p =>
    {
        var info = new FileInfo(p);
        var isDir = Directory.Exists(p);
        return new FileItem(Path.GetFileName(p), PathGuard.Relative(cfg.Arma3Dir, p), isDir, isDir ? 0 : info.Length, info.LastWriteTimeUtc);
    }).Where(i => !ProtectedFiles.IsProtected(i.Path)).OrderByDescending(i => i.IsDir).ThenBy(i => i.Name).ToArray());
    return Results.Json(new { path = PathGuard.Relative(cfg.Arma3Dir, dir), rootName = "Arma 3 Server", items });
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

// ── Serve frontend static files when running without a separate Nginx ────────
// In production the Nginx container serves web/public/. In local WSL2 dev
// mode (no Nginx) the API serves them directly so the panel loads.
var webPublicDir = Path.Combine(AppContext.BaseDirectory, "web", "public");
// Try relative to binary first, then relative to content root (dev mode)
if (!Directory.Exists(webPublicDir))
    webPublicDir = Path.Combine(builder.Environment.ContentRootPath, "web", "public");

if (Directory.Exists(webPublicDir))
{
    var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webPublicDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp, RequestPath = "/static" });
    app.UseStaticFiles(new StaticFileOptions   { FileProvider = fp, RequestPath = "/static" });
    // Also serve index.html at root and for any unmatched route
    app.MapGet("/config.js", () =>
        Results.Content("window.ARMA3_API_BASE = '';\nwindow.ARMA3_REST_ONLY = true;\n",
                        "application/javascript"));
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fp });
}

app.Run();

static bool IsAuthed(HttpContext http) => http.Session.GetString("authenticated") == "true";

static async ValueTask<object?> AuthFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    var http = ctx.HttpContext;
    if (!IsAuthed(http)) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
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

static class ModFileRepair
{
    public static int MakeLowercase(IEnumerable<string> modPaths)
    {
        var changed = 0;
        foreach (var modPath in modPaths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            changed += LowercaseTree(modPath);
        }
        return changed;
    }

    static int LowercaseTree(string root)
    {
        var changed = 0;
        if (Directory.Exists(root))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root).OrderByDescending(p => p.Count(c => c == Path.DirectorySeparatorChar)))
            {
                changed += LowercaseTree(entry);
            }
        }
        changed += LowercaseEntry(root);
        return changed;
    }

    static int LowercaseEntry(string path)
    {
        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name)) return 0;

        var lower = name.ToLowerInvariant();
        if (name == lower) return 0;

        var target = Path.Combine(parent, lower);
        if (Path.Exists(target) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.Ordinal))
            return 0;

        var temp = Path.Combine(parent, $".a3mgr-lower-{Guid.NewGuid():N}");
        if (Directory.Exists(path))
        {
            Directory.Move(path, temp);
            Directory.Move(temp, target);
        }
        else if (File.Exists(path))
        {
            File.Move(path, temp);
            File.Move(temp, target);
        }
        else
        {
            return 0;
        }
        return 1;
    }
}

static class CommandLog
{
    public static string Format(string file, IEnumerable<string> args)
    {
        var rendered = new List<string>();
        foreach (var arg in args)
        {
            rendered.Add(QuoteArg(arg));
        }
        return $"{file} {string.Join(' ', rendered)}";
    }

    static string QuoteArg(string arg) => arg.Any(char.IsWhiteSpace) ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}

record AppConfig(int WebPort, string WebUsername, string WebPassword, string SessionSecret, string Arma3Dir, string SteamUser, string SteamPass, int ServerPort, int ServerMaxPlayers, string BaseUrl, string PublicJoinHost, string[] CreatorDlcAppIds, HashSet<string> SteamOwnerIds, string? FrontendOrigin)
{
    public static AppConfig FromEnvironment() => new(
        int.Parse(Environment.GetEnvironmentVariable("WEB_PORT") ?? "8080"),
        Environment.GetEnvironmentVariable("WEB_USERNAME") ?? "admin",
        Environment.GetEnvironmentVariable("WEB_PASSWORD") ?? "change_this_panel_password",
        Environment.GetEnvironmentVariable("SESSION_SECRET") ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
        Environment.GetEnvironmentVariable("ARMA3_DIR") ?? "/arma3",
        Environment.GetEnvironmentVariable("STEAM_USER") ?? "anonymous",
        Environment.GetEnvironmentVariable("STEAM_PASS") ?? "",
        int.Parse(Environment.GetEnvironmentVariable("SERVER_PORT") ?? "2302"),
        int.Parse(Environment.GetEnvironmentVariable("SERVER_MAX_PLAYERS") ?? "40"),
        Environment.GetEnvironmentVariable("BASE_URL") ?? "",
        Environment.GetEnvironmentVariable("PUBLIC_JOIN_HOST") ?? "",
        (Environment.GetEnvironmentVariable("CREATOR_DLC_APP_IDS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(id => Regex.IsMatch(id, @"^\d+$")).Distinct().ToArray(),
        (Environment.GetEnvironmentVariable("STEAM_OWNER_IDS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(),
        Environment.GetEnvironmentVariable("FRONTEND_ORIGIN"));
}

record ServerPaths(string Arma3Dir, string Arma3Bin, string SteamCmd, string ConfigDir, string ProfilesDir, string MissionsDir, string KeysDir, string WorkshopDir, string ModsRoot)
{
    public static async Task<ServerPaths> DetectAsync(AppConfig cfg)
    {
        var steamInDir = Path.Combine(cfg.Arma3Dir, "steamcmd", "steamcmd.sh");
        var steamGlob = Path.Combine(Environment.GetEnvironmentVariable("STEAMCMD_DIR") ?? "/steamcmd", "steamcmd.sh");
        var steam = File.Exists(steamInDir) ? steamInDir : steamGlob;
        // Prefer 64-bit binary; fall back to 32-bit only if x64 is absent
        var bin = File.Exists(Path.Combine(cfg.Arma3Dir, "arma3server_x64"))
            ? Path.Combine(cfg.Arma3Dir, "arma3server_x64")
            : Path.Combine(cfg.Arma3Dir, "arma3server");
        var config = Environment.GetEnvironmentVariable("CONFIG_DIR") ?? (File.Exists(Path.Combine(cfg.Arma3Dir, "config", "server.cfg")) ? Path.Combine(cfg.Arma3Dir, "config") : cfg.Arma3Dir);
        var profiles = Environment.GetEnvironmentVariable("PROFILES_DIR") ?? (Directory.Exists(Path.Combine(cfg.Arma3Dir, "serverprofile")) ? Path.Combine(cfg.Arma3Dir, "serverprofile") : Path.Combine(cfg.Arma3Dir, "profiles"));
        var missions = Environment.GetEnvironmentVariable("MISSIONS_DIR") ?? (Directory.Exists(Path.Combine(cfg.Arma3Dir, "mpmissions")) ? Path.Combine(cfg.Arma3Dir, "mpmissions") : Path.Combine(cfg.Arma3Dir, "missions"));
        Directory.CreateDirectory(cfg.Arma3Dir);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(missions);
        Directory.CreateDirectory(Path.Combine(cfg.Arma3Dir, "keys"));
        await SeedDefaultConfigAsync(config);
        await Task.CompletedTask;
        return new(cfg.Arma3Dir, bin, steam, config, profiles, missions, Path.Combine(cfg.Arma3Dir, "keys"), Path.Combine(cfg.Arma3Dir, "steamapps", "workshop", "content", "107410"), cfg.Arma3Dir);
    }

    static async Task SeedDefaultConfigAsync(string configDir)
    {
        var defaults = "/defaults/config";
        if (!Directory.Exists(defaults)) return;
        foreach (var file in new[] { "server.cfg", "basic.cfg" })
        {
            var dest = Path.Combine(configDir, file);
            var src = Path.Combine(defaults, file);
            if (!File.Exists(dest) && File.Exists(src))
            {
                await using var input = File.OpenRead(src);
                await using var output = File.Create(dest);
                await input.CopyToAsync(output);
            }
        }
    }
}

record StartupSettings(string ServerBinary, string Ip, int Port, string ProfilesDir, string ServerCfg, string BasicCfg, string ExtraParams, int? MaxPlayers, string ServerPassword, bool AutomaticUpdates, bool DownloadCreatorDlcs, bool LowerCaseMods, bool ValidateServerFiles, bool DisableBattleEye, string ServerMods, string OptionalClientMods, int[] ExtraPorts, int HeadlessClients, string SteamCmdFlags)
{
    public StartupSettings Normalized(ServerPaths paths, AppConfig cfg) => this with
    {
        ServerBinary = "arma3server_x64",
        Ip = string.IsNullOrWhiteSpace(Ip) ? "0.0.0.0" : Ip,
        Port = Port is < 1 or > 65535 ? cfg.ServerPort : Port,
        ProfilesDir = string.IsNullOrWhiteSpace(ProfilesDir) ? paths.ProfilesDir : ProfilesDir,
        ServerCfg = string.IsNullOrWhiteSpace(ServerCfg) ? Path.Combine(paths.ConfigDir, "server.cfg") : ServerCfg,
        BasicCfg = string.IsNullOrWhiteSpace(BasicCfg) ? Path.Combine(paths.ConfigDir, "basic.cfg") : BasicCfg,
        MaxPlayers = MaxPlayers ?? cfg.ServerMaxPlayers,
        ExtraPorts = ExtraPorts.Where(p => p is > 0 and < 65536).Distinct().ToArray(),
        HeadlessClients = Math.Clamp(HeadlessClients, 0, 5)
    };

    public static StartupSettings Default(ServerPaths paths, AppConfig cfg) => new("arma3server_x64", "0.0.0.0", cfg.ServerPort, paths.ProfilesDir, Path.Combine(paths.ConfigDir, "server.cfg"), Path.Combine(paths.ConfigDir, "basic.cfg"), "-autoInit -preload -limitFPS=120 -bandwidthAlg=2 -maxFileCacheSize -noSound", cfg.ServerMaxPlayers, "", false, false, false, false, false, "", "", [], 0, "");
}

record Mod(string Id, string Name, string Path, bool Active, string? WorkshopId);
record SteamAuth(string Username, DateTimeOffset UpdatedAt);
record ModlistState(string? ActiveModlistId, List<Modlist> Lists);
record Modlist(string Id, string Name, List<PresetMod> Mods, DateTimeOffset CreatedAt);
record PresetMod(string Name, string WorkshopId);
record LogEntry(string Type, string Data, DateTimeOffset Ts);
record FileItem(string Name, string Path, bool IsDir, long Size, DateTime Modified);
record SavedPresetFile(string Name, string Path, long Size, DateTime Modified);
record LoginRequest(string Username, string Password);
record ModUpdate(bool Active);
record InstallModRequest(string WorkshopId, string? Name);
record InstallBatchRequest(List<PresetMod> Mods);
record ModlistSaveRequest(string Name, List<PresetMod> Mods, bool Activate);
record PresetFileLoadRequest(string Path);
record CreatorDlcUpdate(bool Active);
record CreatorDlc(string Id, string Name, string Folder, string Path, bool Available, bool Active);

static class PasswordHasher
{
    const int Iterations = 100_000;
    public static PanelAuth Create(string username, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new(username, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string salt, string expectedHash)
    {
        var saltBytes = Convert.FromBase64String(salt);
        var expected = Convert.FromBase64String(expectedHash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

static class MetricsReader
{
    public static object ReadMemory()
    {
        var used = ReadLong("/sys/fs/cgroup/memory.current") ?? Process.GetCurrentProcess().WorkingSet64;
        var total = ReadLong("/sys/fs/cgroup/memory.max") ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0) total = used;

        var free = Math.Max(total - used, 0);
        var percent = total > 0 ? Math.Clamp(Math.Round(used * 100d / total, 1), 0, 100) : 0;
        return new { total, used, free, percent };
    }

    public static object ReadDisk(string path, string mountLabel)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);

        var drive = new DriveInfo(Path.GetPathRoot(fullPath) ?? "/");
        var size = drive.TotalSize;
        var available = drive.AvailableFreeSpace;
        var used = size - available;
        var percent = size > 0 ? Math.Clamp(Math.Round(used * 100d / size, 1), 0, 100) : 0;

        return new
        {
            fs = drive.Name,
            mount = mountLabel,
            size,
            used,
            available,
            percent
        };
    }

    static long? ReadLong(string path)
    {
        if (!File.Exists(path)) return null;
        var value = File.ReadAllText(path).Trim();
        if (value.Equals("max", StringComparison.OrdinalIgnoreCase)) return null;
        return long.TryParse(value, out var parsed) ? parsed : null;
    }
}

static class CreatorDlcCatalog
{
    static readonly (string Id, string Name, string Folder)[] Known =
    [
        ("gm", "Global Mobilization", "gm"),
        ("vn", "S.O.G. Prairie Fire", "vn"),
        ("csla", "CSLA Iron Curtain", "csla"),
        ("ws", "Western Sahara", "ws"),
        ("spe", "Spearhead 1944", "spe"),
        ("rf", "Reaction Forces", "rf"),
        ("ef", "Expeditionary Forces", "ef")
    ];

    public static List<CreatorDlc> List(AppConfig cfg) => Known
        .Select(x =>
        {
            var path = Path.Combine(cfg.Arma3Dir, x.Folder);
            return new CreatorDlc(x.Id, x.Name, x.Folder, path, Directory.Exists(path), false);
        })
        .ToList();
}

record FileWriteRequest(string Path, string Content);
record FileRenameRequest(string Path, string NewName);
record ConfigWriteRequest(string File, string Content);
record SteamLoginRequest(string Username, string Password);
record SteamInputRequest(string Input);
record AccountUpdateRequest(string Username, string CurrentPassword, string NewPassword);
record PanelAuth(string Username, string PasswordSalt, string PasswordHash);

class SqliteStore(string dbPath)
{
    public string DbPath => dbPath;
    readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var cx = new SqliteConnection($"Data Source={dbPath}");
        cx.Open();
        return cx;
    }

    public async Task InitAsync()
    {
        await using var cx = Open();
        var cmd = cx.CreateCommand();
        cmd.CommandText = """
        create table if not exists settings (key text primary key, value text not null);
        create table if not exists mods (id text primary key, name text not null, path text not null, active integer not null, workshop_id text null);
        create table if not exists modlists (id text primary key, name text not null, mods_json text not null, created_at text not null);
        create table if not exists app_state (key text primary key, value text not null);
        create table if not exists task_history (id integer primary key autoincrement, kind text not null, command text not null, exit_code integer null, created_at text not null);
        """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MigrateJsonStateAsync(ServerPaths paths)
    {
        var startupPath = Path.Combine(paths.Arma3Dir, "startup.json");
        if (File.Exists(startupPath) && await GetRawSettingAsync("startup") is null)
            await SetRawSettingAsync("startup", await File.ReadAllTextAsync(startupPath));
        var authPath = Path.Combine(paths.Arma3Dir, "steamcmd-auth.json");
        if (File.Exists(authPath) && await GetRawStateAsync("steamcmd-auth") is null)
            await SetRawStateAsync("steamcmd-auth", await File.ReadAllTextAsync(authPath));
    }

    public async Task<StartupSettings> GetStartupAsync(ServerPaths paths, AppConfig cfg)
    {
        var raw = await GetRawSettingAsync("startup");
        return raw is null ? StartupSettings.Default(paths, cfg) : JsonSerializer.Deserialize<StartupSettings>(raw, json)!.Normalized(paths, cfg);
    }

    public Task SaveStartupAsync(StartupSettings settings) => SetRawSettingAsync("startup", JsonSerializer.Serialize(settings, json));
    public async Task<PanelAuth> GetPanelAuthAsync(AppConfig cfg)
    {
        var raw = await GetRawStateAsync("panel-auth");
        return raw is null ? PasswordHasher.Create(cfg.WebUsername, cfg.WebPassword) : JsonSerializer.Deserialize<PanelAuth>(raw, json)!;
    }
    public Task SavePanelAuthAsync(PanelAuth auth) => SetRawStateAsync("panel-auth", JsonSerializer.Serialize(auth, json));
    public async Task<SteamAuth?> GetSteamAuthAsync()
    {
        var raw = await GetRawStateAsync("steamcmd-auth");
        return raw is null ? null : JsonSerializer.Deserialize<SteamAuth>(raw, json);
    }
    public Task SaveSteamAuthAsync(string username) => SetRawStateAsync("steamcmd-auth", JsonSerializer.Serialize(new SteamAuth(username, DateTimeOffset.UtcNow), json));

    public async Task<List<CreatorDlc>> GetCreatorDlcsAsync(AppConfig cfg)
    {
        var active = await GetActiveCreatorDlcIdsAsync();
        return CreatorDlcCatalog.List(cfg)
            .Select(d => d with { Active = active.Contains(d.Id) && d.Available })
            .ToList();
    }
    public async Task<CreatorDlc?> SetCreatorDlcActiveAsync(AppConfig cfg, string id, bool active)
    {
        var dlcs = CreatorDlcCatalog.List(cfg);
        var selected = dlcs.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (selected is null) return null;
        var activeIds = await GetActiveCreatorDlcIdsAsync();
        activeIds.RemoveWhere(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (active && selected.Available) activeIds.Add(selected.Id);
        await SetRawStateAsync("creator-dlcs-active", JsonSerializer.Serialize(activeIds.Order().ToArray(), json));
        return selected with { Active = active && selected.Available };
    }
    public async Task<List<string>> GetActiveCreatorDlcPathsAsync(AppConfig cfg) =>
        (await GetCreatorDlcsAsync(cfg)).Where(d => d.Active && d.Available).Select(d => d.Path).ToList();

    async Task<HashSet<string>> GetActiveCreatorDlcIdsAsync()
    {
        var raw = await GetRawStateAsync("creator-dlcs-active");
        return raw is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(JsonSerializer.Deserialize<string[]>(raw, json) ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<Mod>> SyncAndGetModsAsync(string root)
    {
        var mods = await GetModsAsync();
        mods = mods.Where(m => string.IsNullOrWhiteSpace(m.Path) || Directory.Exists(m.Path)).ToList();
        foreach (var dir in Directory.Exists(root) ? Directory.EnumerateDirectories(root, "@*") : [])
            if (mods.All(m => Path.GetFullPath(m.Path) != Path.GetFullPath(dir)))
                mods.Add(new Mod(Guid.NewGuid().ToString("n"), Path.GetFileName(dir), dir, true, null));
        await ReplaceModsAsync(mods);
        return mods;
    }
    public async Task<List<string>> GetActiveModsAsync() => (await GetModsAsync()).Where(m => m.Active).Select(m => m.Path).ToList();
    public async Task<Mod?> SetModActiveAsync(string id, bool active)
    {
        var mods = await GetModsAsync();
        var mod = mods.FirstOrDefault(m => m.Id == id);
        if (mod is null) return null;
        mod = mod with { Active = active };
        await ReplaceModsAsync(mods.Select(m => m.Id == id ? mod : m));
        return mod;
    }
    public async Task UpsertModAsync(Mod mod)
    {
        var mods = await GetModsAsync();
        mods.RemoveAll(m => m.WorkshopId == mod.WorkshopId || Path.GetFullPath(m.Path) == Path.GetFullPath(mod.Path));
        mods.Add(mod);
        await ReplaceModsAsync(mods);
    }
    public async Task<Mod?> DeleteModAsync(string id)
    {
        var mods = await GetModsAsync();
        var mod = mods.FirstOrDefault(m => m.Id == id);
        if (mod is null) return null;
        mods.RemoveAll(m => m.Id == id);
        await ReplaceModsAsync(mods);
        return mod;
    }
    public async Task<int> RemoveModsForDeletedPathAsync(string target)
    {
        var targetFull = Path.GetFullPath(target);
        var mods = await GetModsAsync();
        var keep = mods.Where(m =>
        {
            var modFull = Path.GetFullPath(m.Path);
            return !modFull.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase) && !targetFull.StartsWith(modFull, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        await ReplaceModsAsync(keep);
        return mods.Count - keep.Count;
    }

    public async Task<ModlistState> GetModlistsAsync()
    {
        await using var cx = Open();
        var lists = new List<Modlist>();
        var cmd = cx.CreateCommand();
        cmd.CommandText = "select id,name,mods_json,created_at from modlists order by created_at desc";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            lists.Add(new(r.GetString(0), r.GetString(1), JsonSerializer.Deserialize<List<PresetMod>>(r.GetString(2), json) ?? [], DateTimeOffset.Parse(r.GetString(3))));
        return new(await GetRawStateAsync("active-modlist"), lists);
    }
    public async Task<Modlist> SaveModlistAsync(ModlistSaveRequest req)
    {
        var list = new Modlist(Guid.NewGuid().ToString("n"), string.IsNullOrWhiteSpace(req.Name) ? $"Modlist {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}" : req.Name, req.Mods, DateTimeOffset.UtcNow);
        await using var cx = Open();
        var cmd = cx.CreateCommand();
        cmd.CommandText = "insert into modlists(id,name,mods_json,created_at) values($id,$name,$mods,$created)";
        cmd.Parameters.AddWithValue("$id", list.Id); cmd.Parameters.AddWithValue("$name", list.Name); cmd.Parameters.AddWithValue("$mods", JsonSerializer.Serialize(list.Mods, json)); cmd.Parameters.AddWithValue("$created", list.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
        if (req.Activate) await ActivateModlistAsync(list.Id);
        return list;
    }
    public async Task<ModlistState> ActivateModlistAsync(string id)
    {
        await SetRawStateAsync("active-modlist", id);
        var state = await GetModlistsAsync();
        var active = state.Lists.FirstOrDefault(l => l.Id == id);
        if (active is not null)
        {
            var mods = await GetModsAsync();
            var ids = active.Mods.Select(m => m.WorkshopId).ToHashSet();
            await ReplaceModsAsync(mods.Select(m => m.WorkshopId is null ? m : m with { Active = ids.Contains(m.WorkshopId) }));
        }
        return state;
    }
    public async Task DeleteModlistAsync(string id)
    {
        await using var cx = Open();
        var cmd = cx.CreateCommand();
        cmd.CommandText = "delete from modlists where id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task<int> DeleteModsForModlistAsync(string id)
    {
        var state = await GetModlistsAsync();
        var list = state.Lists.FirstOrDefault(l => l.Id == id);
        if (list is null) return 0;
        var ids = list.Mods.Select(m => m.WorkshopId).ToHashSet();
        var mods = await GetModsAsync();
        var delete = mods.Where(m => m.WorkshopId is not null && ids.Contains(m.WorkshopId)).ToList();
        foreach (var mod in delete) if (Directory.Exists(mod.Path)) Directory.Delete(mod.Path, true);
        await ReplaceModsAsync(mods.Except(delete));
        return delete.Count;
    }

    async Task<List<Mod>> GetModsAsync()
    {
        await using var cx = Open();
        var cmd = cx.CreateCommand();
        cmd.CommandText = "select id,name,path,active,workshop_id from mods";
        var outList = new List<Mod>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) outList.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) == 1, r.IsDBNull(4) ? null : r.GetString(4)));
        return outList;
    }
    async Task ReplaceModsAsync(IEnumerable<Mod> mods)
    {
        await using var cx = Open();
        await using var tx = await cx.BeginTransactionAsync();
        var del = cx.CreateCommand(); del.CommandText = "delete from mods"; await del.ExecuteNonQueryAsync();
        foreach (var m in mods)
        {
            var cmd = cx.CreateCommand();
            cmd.CommandText = "insert into mods(id,name,path,active,workshop_id) values($id,$name,$path,$active,$wid)";
            cmd.Parameters.AddWithValue("$id", m.Id); cmd.Parameters.AddWithValue("$name", m.Name); cmd.Parameters.AddWithValue("$path", m.Path); cmd.Parameters.AddWithValue("$active", m.Active ? 1 : 0); cmd.Parameters.AddWithValue("$wid", (object?)m.WorkshopId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
    Task<string?> GetRawSettingAsync(string key) => GetRawAsync("settings", key);
    Task SetRawSettingAsync(string key, string value) => SetRawAsync("settings", key, value);
    Task<string?> GetRawStateAsync(string key) => GetRawAsync("app_state", key);
    Task SetRawStateAsync(string key, string value) => SetRawAsync("app_state", key, value);
    async Task<string?> GetRawAsync(string table, string key)
    {
        await using var cx = Open();
        var cmd = cx.CreateCommand();
        cmd.CommandText = $"select value from {table} where key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return (string?)await cmd.ExecuteScalarAsync();
    }
    async Task SetRawAsync(string table, string key, string value)
    {
        await using var cx = Open();
        var cmd = cx.CreateCommand();
        cmd.CommandText = $"insert into {table}(key,value) values($key,$value) on conflict(key) do update set value=excluded.value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync();
    }
}

class RuntimeState
{
    Process? proc;
    readonly List<LogEntry> logs = [];
    readonly SemaphoreSlim taskGate = new(1, 1);
    public IReadOnlyList<LogEntry> Logs => logs;
    public bool IsRunning => proc is { HasExited: false };
    public int? ProcessId => IsRunning ? proc!.Id : null;
    public void Start(string file, IEnumerable<string> args, string cwd)
    {
        proc = StartProcess(file, args, cwd);
        Push("system", $"Started {file} PID {proc.Id}");
        proc.Exited += (_, _) => Push("system", $"Server exited with code {proc.ExitCode}");
    }
    public void Stop()
    {
        if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true);
        Push("system", "Server stopped");
    }
    public void RunTask(string file, IEnumerable<string> args, string kind, Func<int, Task>? onExit = null)
    {
        var capturedArgs = args.ToArray();
        _ = Task.Run(async () =>
        {
            await taskGate.WaitAsync();
            try
            {
                using var p = StartProcess(file, capturedArgs, Directory.GetCurrentDirectory());
                Push("system", $"Task {kind} started PID {p.Id}");
                Push("system", $"Command: {CommandLog.Format(file, RedactSteamArgs(capturedArgs))}");
                await p.WaitForExitAsync();
                Push("system", $"Task {kind} exited with code {p.ExitCode}");
                if (onExit is not null) await onExit(p.ExitCode);
            }
            catch (Exception ex)
            {
                Push("stderr", $"Task {kind} failed: {ex.Message}");
            }
            finally
            {
                taskGate.Release();
            }
        });
    }
    static IEnumerable<string> RedactSteamArgs(string[] args)
    {
        var redactingLogin = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "+login")
            {
                redactingLogin = true;
                yield return args[i];
                continue;
            }
            if (redactingLogin && args[i].StartsWith('+'))
            {
                redactingLogin = false;
            }
            yield return redactingLogin ? "***" : args[i];
        }
    }
    Process StartProcess(string file, IEnumerable<string> args, string cwd)
    {
        var p = new Process { StartInfo = new(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }, EnableRaisingEvents = true };
        foreach (var arg in args) p.StartInfo.ArgumentList.Add(arg);
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Push("stdout", e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Push("stderr", e.Data); };
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }
    public void Push(string type, string data)
    {
        logs.Add(new(type, data, DateTimeOffset.UtcNow));
        if (logs.Count > 1000) logs.RemoveAt(0);
    }
}

class SteamCmdSession(ServerPaths paths)
{
    Process? proc;
    string? username;
    bool awaitingInput;
    string? lastError;
    readonly List<LogEntry> logs = [];
    public static object EmptyPublicState() => new { running = false, awaitingInput = false, username = (string?)null, exitCode = (int?)null, lastError = (string?)null, logs = Array.Empty<LogEntry>() };
    public static bool HasCachedLogin(string? expectedUsername = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "Steam", "config", "loginusers.vdf"),
            Path.Combine(home, "Steam", "config", "config.vdf"),
            Path.Combine(home, ".steam", "steam", "config", "loginusers.vdf"),
            Path.Combine(home, ".steam", "steam", "config", "config.vdf")
        };
        foreach (var candidate in candidates.Where(File.Exists))
        {
            string text;
            try { text = File.ReadAllText(candidate); }
            catch { continue; }
            if (!string.IsNullOrWhiteSpace(expectedUsername) && text.Contains(expectedUsername, StringComparison.OrdinalIgnoreCase))
                return true;
            if (text.Contains("loginkey", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("refreshtoken", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("\"accounts\"", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
    public static void ResetCache()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[]
        {
            Path.Combine(home, "Steam"),
            Path.Combine(home, ".steam")
        })
        {
            ClearDirectory(dir);
        }
    }
    static void ClearDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var full = Path.GetFullPath(dir);
        var home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!full.StartsWith(home, StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to reset SteamCMD outside the user home");
        foreach (var file in Directory.EnumerateFiles(full)) File.Delete(file);
        foreach (var child in Directory.EnumerateDirectories(full)) Directory.Delete(child, true);
    }
    public Task<object> PublicStateAsync(bool includeLogs = false) => Task.FromResult<object>(new { running = proc is { HasExited: false }, awaitingInput, username, exitCode = proc?.HasExited == true ? proc.ExitCode : (int?)null, lastError, logs = includeLogs ? logs : [] });
    public Task StartAsync(string username, string password)
    {
        if (proc is { HasExited: false }) throw new InvalidOperationException("SteamCMD login is already running");
        this.username = username;
        awaitingInput = false;
        lastError = null;
        logs.Clear();
        var args = new List<string> { "+login", username };
        if (!string.IsNullOrWhiteSpace(password)) args.Add(password);
        args.Add("+quit");
        proc = new Process { StartInfo = new(paths.SteamCmd) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }, EnableRaisingEvents = true };
        foreach (var arg in args) proc.StartInfo.ArgumentList.Add(arg);
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Push("stdout", e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Push("stderr", e.Data); };
        proc.Exited += (_, _) => awaitingInput = false;
        proc.Start(); proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        return Task.CompletedTask;
    }
    public void Write(string input)
    {
        if (proc is { HasExited: false })
        {
            proc.StandardInput.WriteLine(input);
            awaitingInput = false;
        }
    }
    public void Cancel() { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); }
    void Push(string type, string data)
    {
        logs.Add(new(type, data, DateTimeOffset.UtcNow));
        if (logs.Count > 300) logs.RemoveAt(0);
        var text = data.ToLowerInvariant();
        if (text.Contains("steam guard") || text.Contains("two-factor") || text.Contains("auth code") || text.Contains("password:"))
            awaitingInput = true;
        if (text.Contains("logged in ok") || text.Contains("update state") || text.Contains("unloading steam api"))
            awaitingInput = false;
        if (text.Contains("error") || text.Contains("invalid password") || text.Contains("failed"))
            lastError = data;
    }
}

static class CommandBuilder
{
    public static string Build(ServerPaths paths, StartupSettings s, IEnumerable<string>? mods = null) => "./" + s.ServerBinary + " " + string.Join(' ', Args(paths, s, mods ?? []).Select(Quote));
    public static IEnumerable<string> Args(ServerPaths paths, StartupSettings s, IEnumerable<string> mods)
    {
        // Only set -ip= when binding to a specific interface (not 0.0.0.0).
        // Omitting -ip= when binding-all is more reliable for Steam server browser registration.
        var ip = (s.Ip ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0")
            yield return $"-ip={ip}";
        yield return $"-port={s.Port}";
        yield return $"-config={s.ServerCfg}";
        yield return $"-cfg={s.BasicCfg}";
        yield return $"-profiles={s.ProfilesDir}";
        yield return "-noSplash"; yield return "-noPause"; yield return "-world=empty";
        var modList = mods.Select(m => Path.GetRelativePath(paths.Arma3Dir, m)).ToArray();
        if (modList.Length > 0) yield return $"-mod={string.Join(';', modList)}";
        if (!string.IsNullOrWhiteSpace(s.ServerMods)) yield return $"-serverMod={s.ServerMods}";
        if (s.DisableBattleEye) yield return "-noBattlEye";
        foreach (var arg in SplitArgs(s.ExtraParams)) yield return arg;
    }
    public static IEnumerable<string> SplitArgs(string value) => Regex.Matches(value ?? "", @"[^\s""]+|""([^""]*)""").Select(m => m.Value.Trim('"'));
    static string Quote(string arg) => arg.Any(char.IsWhiteSpace) ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}

static class PathGuard
{
    public static string Resolve(string root, string? reqPath)
    {
        var fullRoot = Path.GetFullPath(root);
        var raw = (reqPath ?? "").Trim();
        var candidate = Path.IsPathRooted(raw) ? raw : Path.Combine(fullRoot, raw);
        var resolved = Path.GetFullPath(candidate);
        if (!resolved.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Access denied");
        return resolved;
    }
    public static string Relative(string root, string fullPath) => Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(fullPath)).Replace('\\', '/') is "." ? "" : Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(fullPath)).Replace('\\', '/');
}

static class PresetFiles
{
    public static string Root(AppConfig cfg) => Path.Combine(cfg.Arma3Dir, "presets", "modlists");

    public static async Task<string> SaveAsync(AppConfig cfg, IFormFile file)
    {
        Directory.CreateDirectory(Root(cfg));
        var name = SafeName(string.IsNullOrWhiteSpace(file.FileName) ? "preset.html" : file.FileName);
        if (!name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            name += ".html";
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(Root(cfg), $"{stamp}-{name}");
        await using var output = File.Create(path);
        await file.CopyToAsync(output);
        return path;
    }

    public static SavedPresetFile[] List(AppConfig cfg)
    {
        Directory.CreateDirectory(Root(cfg));
        return Directory.EnumerateFiles(Root(cfg), "*.htm*")
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new SavedPresetFile(info.Name, PathGuard.Relative(cfg.Arma3Dir, path), info.Length, info.LastWriteTimeUtc);
            })
            .OrderByDescending(f => f.Modified)
            .ToArray();
    }

    public static string Resolve(AppConfig cfg, string path)
    {
        var file = PathGuard.Resolve(cfg.Arma3Dir, path);
        var root = Path.GetFullPath(Root(cfg));
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Access denied");
        if (!File.Exists(full)) throw new FileNotFoundException("Preset file not found");
        return full;
    }

    static string SafeName(string name)
    {
        var clean = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars()) clean = clean.Replace(c, '-');
        return Regex.Replace(clean, @"\s+", "-");
    }
}

static class ProtectedFiles
{
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "manager.sqlite3",
        "manager.sqlite3-shm",
        "manager.sqlite3-wal"
    };

    public static bool IsProtected(string relativePath)
    {
        var clean = relativePath.Replace('\\', '/').Trim('/');
        return Names.Contains(clean);
    }
}

static class PresetParser
{
    public static List<PresetMod> Parse(string html)
    {
        var ids = Regex.Matches(html, @"[?&]id=(\d{6,12})").Select(m => m.Groups[1].Value).Distinct();
        return ids.Select(id => new PresetMod($"@{id}", id)).ToList();
    }
}

static class ServerCfgWriter
{
    public static async Task ApplyAsync(StartupSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.ServerCfg) || !File.Exists(s.ServerCfg)) return;
        var text = await File.ReadAllTextAsync(s.ServerCfg);
        if (s.MaxPlayers is not null) text = Regex.Replace(text, @"maxPlayers\s*=\s*\d+\s*;", $"maxPlayers = {s.MaxPlayers};");
        if (s.ServerPassword is not null) text = Regex.Replace(text, @"password\s*=\s*""[^""]*""\s*;", $"password = \"{s.ServerPassword}\";");
        await File.WriteAllTextAsync(s.ServerCfg, text);
    }
}
