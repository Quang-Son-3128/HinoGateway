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
// is a file the engineer can read, diff and back up — not a schema that needs migrating in the
// field. The store is a singleton: it serves reads from memory and writes the file on change.
//
// A relative path is resolved against the folder the application itself lives in — deliberately NOT
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

// --- Engine composition ------------------------------------------------------
builder.Services.AddSingleton<TagStore>();
builder.Services.AddSingleton<DeviceEngine>();
// Alarm evaluation is hooked into the tag stream by EngineHostedService at startup.
builder.Services.AddSingleton<AlarmEngine>();

// Driver factories — one registration per driver package. Add S7/IEC104/... here later.
builder.Services.AddSingleton<IDeviceDriverFactory, SimulatorDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, HinoGateway.Drivers.Modbus.ModbusTcpDriverFactory>();

// Background services: start the engine, and bridge its updates to SignalR.
builder.Services.AddHostedService<EngineHostedService>();
builder.Services.AddHostedService<TelemetryBroadcaster>();
builder.Services.AddHostedService<AlarmService>();

// --- Northbound: IEC-104 controlled station (the link to the utility) --------
// Registered as a singleton as well as a hosted service so the config API can restart it.
builder.Services.AddSingleton<Iec104HostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Iec104HostedService>());

var app = builder.Build();

// Serve the Blazor WebAssembly client that this project references.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapHub<TelemetryHub>("/hubs/telemetry");
app.MapHub<AlarmHub>("/hubs/alarms");

// SPA fallback: any unmatched route serves the Blazor host page.
app.MapFallbackToFile("index.html");

app.Run();
