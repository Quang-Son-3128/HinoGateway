using Microsoft.AspNetCore.Authentication;
using HinoGateway.Server;
using HinoGateway.Server.Security;
using HinoGateway.Core;
using HinoGateway.Drivers.Simulator;
using HinoGateway.Engine;
using HinoGateway.Server.Hubs;
using HinoGateway.Server.Services;
using HinoGateway.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSignalR();

// --- Configuration: one JSON project file, no database ------------------------
// A gateway has to be deployable by copying a folder onto an industrial PC, so the configuration
// is a file the engineer can read, diff and back up â€” not a schema that needs migrating in the
// field. The store is a singleton: it serves reads from memory and writes the file on change.
//
// A relative path is resolved against the folder the application itself lives in â€” deliberately NOT
// the content root or the working directory, which differ depending on how the gateway was started
// and would leave two configuration files quietly disagreeing with each other. The project file
// travels with the binaries; set Config:Path to an absolute path to keep it elsewhere.
var projectPath = builder.Configuration["Config:Path"] ?? "hino.json";
builder.Services.AddSingleton(sp => new JsonProjectStore(
    System.IO.Path.IsPathRooted(projectPath)
        ? projectPath
        : System.IO.Path.Combine(AppContext.BaseDirectory, projectPath),
    sp.GetRequiredService<ILogger<JsonProjectStore>>()));
builder.Services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<JsonProjectStore>());
builder.Services.AddSingleton<IAlarmStore>(sp => sp.GetRequiredService<JsonProjectStore>());

// The local alarm trail is in memory on purpose: the durable record belongs to the upstream server.
builder.Services.AddSingleton<IAlarmJournal>(sp => new InMemoryAlarmJournal(
    builder.Configuration.GetValue<int?>("Alarms:JournalCapacity") ?? 2000));

// Retentive memory tags keep their value in a sidecar beside the project. It is operational state,
// not configuration: it changes whenever an operator moves a setpoint, and it must not travel inside
// a project export. Same path rules as the project file.
// (Re-added 2026-08-10 while rebuilding after the source loss.)
var retainedPath = builder.Configuration["Config:RetainedPath"] ?? "hino.retain.json";
builder.Services.AddSingleton(sp => new RetainedValueStore(
    System.IO.Path.IsPathRooted(retainedPath)
        ? retainedPath
        : System.IO.Path.Combine(AppContext.BaseDirectory, retainedPath),
    sp.GetRequiredService<ILogger<RetainedValueStore>>()));
builder.Services.AddSingleton<IRetainedValueStore>(sp => sp.GetRequiredService<RetainedValueStore>());

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

var app = builder.Build();

// Serve the Blazor WebAssembly client that this project references.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<TelemetryHub>("/hubs/telemetry");
app.MapHub<AlarmHub>("/hubs/alarms");

// SPA fallback: any unmatched route serves the Blazor host page.
app.MapFallbackToFile("index.html");

app.Run();
