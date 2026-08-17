using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using HinoGateway.Server;
using HinoGateway.Server.Security;
using HinoGateway.Core;
using HinoGateway.Drivers.Simulator;
using HinoGateway.Engine;
using HinoGateway.Server.Hubs;
using HinoGateway.Server.Services;
using HinoGateway.Storage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var dataPaths = GatewayDataPaths.Create(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(dataPaths);
builder.Services.AddWindowsService(options => options.ServiceName = "HinoGateway");
builder.Services.AddSystemd();
builder.Host.UseSerilog((context, services, logging) => logging
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(dataPaths.Logs, "hinogateway-.log"), rollingInterval: RollingInterval.Day,
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: Math.Max(1, context.Configuration.GetValue<int?>("Serilog:File:FileSizeLimitMb") ?? 32) * 1024L * 1024L,
        retainedFileCountLimit: context.Configuration.GetValue<int?>("Serilog:File:RetainedFileCountLimit") ?? 14,
        shared: true));

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ComponentHealthRegistry>();
builder.Services.AddSingleton<AuditTrail>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditTrail>());
builder.Services.AddHealthChecks()
    .AddCheck<GatewayLivenessCheck>("gateway-live", tags: ["live"])
    .AddCheck<GatewayReadinessCheck>("gateway-ready", tags: ["ready"]);

var commandRatePerSecond = Math.Clamp(
    builder.Configuration.GetValue<int?>("Commands:RateLimitPerSecond") ?? 20, 1, 10_000);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("commands", context => RateLimitPartition.GetTokenBucketLimiter(
        partitionKey: context.User.Identity?.Name
                      ?? context.Connection.RemoteIpAddress?.ToString()
                      ?? "unknown",
        factory: _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = commandRatePerSecond,
            TokensPerPeriod = commandRatePerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = 0,
        }));
});

// --- Configuration: one JSON project file, no database ------------------------
// A gateway has to be deployable by copying a folder onto an industrial PC, so the configuration
// is a file the engineer can read, diff and back up â€” not a schema that needs migrating in the
// field. The store is a singleton: it serves reads from memory and writes the file on change.
//
// A relative path is resolved against the folder the application itself lives in â€” deliberately NOT
// the content root or the working directory, which differ depending on how the gateway was started
// and would leave two configuration files quietly disagreeing with each other. The project file
// travels with the binaries; set Config:Path to an absolute path to keep it elsewhere.
var projectPath = dataPaths.Resolve(builder.Configuration["Config:Path"], dataPaths.Project);
builder.Services.AddSingleton(sp => new JsonProjectStore(
    projectPath,
    sp.GetRequiredService<ILogger<JsonProjectStore>>()));
builder.Services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<JsonProjectStore>());
builder.Services.AddSingleton<IAlarmStore>(sp => sp.GetRequiredService<JsonProjectStore>());

var secretsPath = dataPaths.Resolve(builder.Configuration["Config:SecretsPath"], dataPaths.Secrets);
builder.Services.AddSingleton(sp => new JsonSecretStore(
    secretsPath,
    sp.GetRequiredService<ILogger<JsonSecretStore>>()));
builder.Services.AddSingleton<ISecretStore>(sp => sp.GetRequiredService<JsonSecretStore>());

// The local alarm trail is in memory on purpose: the durable record belongs to the upstream server.
builder.Services.AddSingleton<IAlarmJournal>(sp => new InMemoryAlarmJournal(
    builder.Configuration.GetValue<int?>("Alarms:JournalCapacity") ?? 2000));

// Retentive memory tags keep their value in a sidecar beside the project. It is operational state,
// not configuration: it changes whenever an operator moves a setpoint, and it must not travel inside
// a project export. Same path rules as the project file.
// (Re-added 2026-08-10 while rebuilding after the source loss.)
var retainedPath = dataPaths.Resolve(builder.Configuration["Config:RetainedPath"], dataPaths.Retained);
builder.Services.AddSingleton(sp => new RetainedValueStore(
    retainedPath,
    sp.GetRequiredService<ILogger<RetainedValueStore>>()));
builder.Services.AddSingleton<IRetainedValueStore>(sp => sp.GetRequiredService<RetainedValueStore>());

// --- Local history -----------------------------------------------------------
// Decision 378/QĐ-EVN requires a site to keep its measurements locally at a resolution of at most
// five minutes for at least seven days. The same file is what lets an accumulator recover the
// baseline it was holding when the power went out, so this is a dependency of the energy totals and
// not merely a compliance box.
var historyOptions = builder.Configuration.GetSection("History").Get<SampleHistoryOptions>() ?? new();
builder.Services.AddSingleton(historyOptions);
builder.Services.AddSingleton<ISampleHistory>(sp => new JsonlSampleHistory(
    dataPaths.Resolve(historyOptions.Path, dataPaths.History),
    historyOptions,
    sp.GetRequiredService<ILogger<JsonlSampleHistory>>()));
builder.Services.AddHostedService<HistorySamplerService>();

// --- Energy accumulation -----------------------------------------------------
// The baselines are a singleton in their own right and deliberately NOT part of the engine runtime:
// "Apply to engine" rebuilds that runtime, and a baseline living there would take the day's energy
// with it every time an operator saved a tag.
var energyPath = dataPaths.Resolve(builder.Configuration["Energy:StatePath"],
    Path.Combine(dataPaths.Root, "hino.energy.json"));
builder.Services.AddSingleton(sp => new AccumulatorStateStore(
    energyPath, sp.GetRequiredService<ILogger<AccumulatorStateStore>>()));
builder.Services.AddSingleton<IAccumulatorStateStore>(sp => sp.GetRequiredService<AccumulatorStateStore>());

var journalOptions = builder.Configuration.GetSection("Energy:Journal").Get<EnergyJournalOptions>() ?? new();
builder.Services.AddSingleton(journalOptions);
builder.Services.AddSingleton<IEnergyJournal>(sp => new JsonlEnergyJournal(
    dataPaths.Resolve(builder.Configuration["Energy:JournalPath"], Path.Combine(dataPaths.Root, "energy")),
    journalOptions,
    sp.GetRequiredService<ILogger<JsonlEnergyJournal>>()));

// --- Security ----------------------------------------------------------------
// Every request gets an identity even when security is switched off, and that identity is Viewer
// unless the request came from the machine itself. Treating an unauthenticated request as a full
// operator would mean anyone who can reach the port can command a PLC.
var security = builder.Configuration.GetSection("Security").Get<GatewaySecurityOptions>()
               ?? new GatewaySecurityOptions();
GatewaySecurityValidation.Validate(security, builder.Configuration);
builder.Services.AddSingleton(security);
builder.Services.AddAuthentication(GatewayApiKeyHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, GatewayApiKeyHandler>(GatewayApiKeyHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    foreach (var policy in new[] { GatewayPolicies.Viewer, GatewayPolicies.Operator, GatewayPolicies.Admin })
    {
        var accepted = GatewayPolicies.AtLeast(policy);
        options.AddPolicy(policy, p => p.RequireAssertion(context =>
            accepted.Any(role => context.User.IsInRole(role))));
    }
});

// --- Engine composition ------------------------------------------------------
builder.Services.AddSingleton<TagStore>();
builder.Services.AddSingleton<DeviceEngine>();
builder.Services.AddSingleton<ProjectValidator>();
// Alarm evaluation is hooked into the tag stream by EngineHostedService at startup.
builder.Services.AddSingleton<AlarmEngine>();
var commandOptions = builder.Configuration.GetSection("Commands").Get<ControlCommandOptions>() ?? new();
builder.Services.AddSingleton(commandOptions);
builder.Services.AddSingleton<ControlCommandService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ControlCommandService>());

// Driver factories â€” one registration per driver package. Add S7/IEC104/... here later.
builder.Services.AddSingleton<IDeviceDriverFactory, SimulatorDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, HinoGateway.Drivers.Modbus.ModbusTcpDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, HinoGateway.Drivers.SiemensS7.SiemensS7DriverFactory>();

// Background services: start the engine, and bridge its updates to SignalR.
builder.Services.AddHostedService<EngineHostedService>();
builder.Services.AddHostedService<TelemetryBroadcaster>();
builder.Services.AddHostedService<AlarmService>();

// --- Northbound: IEC-104 controlled station (the link to the utility) --------
// Registered as a singleton as well as a hosted service so the config API can restart it.
builder.Services.AddSingleton<Iec104HostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Iec104HostedService>());

// MQTT keeps its own durable disk spool and can be reapplied without restarting the gateway.
builder.Services.AddSingleton<MqttHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttHostedService>());
builder.Services.AddHostedService<GatewaySupervisorService>();

var app = builder.Build();
app.Logger.LogInformation("Gateway data root: {DataRoot}", dataPaths.Root);
if (GatewayDataPaths.PermissionWarning is { } permissionWarning)
    app.Logger.LogWarning("{Warning}", permissionWarning);

// Serve the Blazor WebAssembly client that this project references.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

if (security.Enabled && security.RequireHttps)
{
    app.UseHsts();
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("HTTPS is required.");
            return;
        }
        await next(context);
    });
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<AuditMiddleware>();

app.MapControllers();
app.MapHub<TelemetryHub>("/hubs/telemetry").RequireAuthorization(GatewayPolicies.Viewer);
app.MapHub<AlarmHub>("/hubs/alarms").RequireAuthorization(GatewayPolicies.Viewer);
app.MapHealthChecks("/health/live", new HealthCheckOptions
{ Predicate = registration => registration.Tags.Contains("live") }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{ Predicate = registration => registration.Tags.Contains("ready") }).AllowAnonymous();

// SPA fallback: any unmatched route serves the Blazor host page.
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
