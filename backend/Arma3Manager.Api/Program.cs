using Arma3Manager.Api.Application;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Endpoints;
using Arma3Manager.Api.Infrastructure;
using Arma3Manager.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
var config = AppConfig.Load(builder.Environment.ContentRootPath);
await FactoryResetExecutor.ExecutePendingAsync(config);
var paths = await ServerPaths.DetectAsync(config);
var store = new SqliteStore(Path.Combine(config.Arma3Dir, "manager.sqlite3"));
await store.InitAsync();
await store.MigrateJsonStateAsync(paths);
await store.EnsureFileIndexVersionAsync(2);
var storageRepair = WorkshopStorage.RepairDuplicates(config);
await store.NormalizeWorkshopModPathsAsync(config);
if (storageRepair.Converted > 0)
    Console.WriteLine($"Optimized {storageRepair.Converted} duplicated mod folders ({storageRepair.ReclaimedBytes} bytes reclaimed)");

builder.WebHost.UseKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null;
    options.ListenAnyIP(config.WebPort);
});
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<LogHub>();
builder.Services.AddSingleton<RuntimeState>();
builder.Services.AddSingleton<ServerLifecycleCoordinator>();
builder.Services.AddHostedService(services => services.GetRequiredService<ServerLifecycleCoordinator>());
builder.Services.AddSingleton(services => new LogStreamService(
    services.GetRequiredService<RuntimeState>(),
    services.GetRequiredService<ServerLifecycleCoordinator>(),
    services.GetRequiredService<ILogger<LogStreamService>>()));
builder.Services.AddSingleton<SteamCmdSession>();
builder.Services.AddSingleton<BattlEyeRconClient>();
builder.Services.AddSingleton<PlayerActivityService>();
builder.Services.AddHostedService(services => services.GetRequiredService<PlayerActivityService>());
builder.Services.AddSingleton<MetricsSampler>();
builder.Services.AddHostedService(services => services.GetRequiredService<MetricsSampler>());
builder.Services.AddSingleton<FileIndexer>();
builder.Services.AddHostedService(services => services.GetRequiredService<FileIndexer>());
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "a3mgr.sid";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.IdleTimeout = TimeSpan.FromHours(24);
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = long.MaxValue);
builder.Services.AddCors(options => options.AddPolicy("frontend", policy =>
{
    if (string.IsNullOrWhiteSpace(config.FrontendOrigin))
        policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials();
    else
        policy.WithOrigins(config.FrontendOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var app = builder.Build();
await app.Services.GetRequiredService<ServerLifecycleCoordinator>().InitializeAsync();
app.UseCors("frontend");
app.UseSession();
app.MapApiEndpoints(config, paths, store);
app.Run();

public partial class Program;
