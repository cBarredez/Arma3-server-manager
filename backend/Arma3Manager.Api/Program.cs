using Arma3Manager.Api.Application;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Endpoints;
using Arma3Manager.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
var config = AppConfig.Load(builder.Environment.ContentRootPath);
var paths = await ServerPaths.DetectAsync(config);
await BattlEyeConfigWriter.ApplyAsync(paths, config);
var store = new SqliteStore(Path.Combine(config.Arma3Dir, "manager.sqlite3"));
await store.InitAsync();
await store.MigrateJsonStateAsync(paths);

builder.WebHost.UseKestrel(options => options.ListenAnyIP(config.WebPort));
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<RuntimeState>();
builder.Services.AddSingleton<SteamCmdSession>();
builder.Services.AddSingleton<BattlEyeRconClient>();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "a3mgr.sid";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.IdleTimeout = TimeSpan.FromHours(24);
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 500L * 1024 * 1024);
builder.Services.AddCors(options => options.AddPolicy("frontend", policy =>
{
    if (string.IsNullOrWhiteSpace(config.FrontendOrigin))
        policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials();
    else
        policy.WithOrigins(config.FrontendOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var app = builder.Build();
app.UseCors("frontend");
app.UseSession();
app.MapApiEndpoints(config, paths, store);
app.Run();

public partial class Program;
