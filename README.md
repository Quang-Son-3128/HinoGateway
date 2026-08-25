# HinoGateway

HinoGateway is a .NET 8 industrial edge gateway with a Blazor WebAssembly UI. It polls Modbus TCP,
Siemens S7 and simulator connections; evaluates memory/expression tags and alarms; serves live data
over REST/SignalR; and publishes northbound through IEC 60870-5-104 and MQTT.

## Build and run

```powershell
dotnet restore HinoGateway.sln -m:1 -nr:false -p:RestoreDisableParallel=true
dotnet build HinoGateway.sln --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/HinoGateway.Engine.Tests/HinoGateway.Engine.Tests.csproj --no-build -m:1 -nr:false
dotnet run --project src/HinoGateway.Server
```

The development endpoint is `http://localhost:2000`. Development seeds a simulator only when the
project file is genuinely absent. Production starts empty and never silently inserts demo devices.

## Persistent data

`Data:Root` is the single stable state root. It must be absolute when configured.

| Environment | Default root |
| --- | --- |
| Development | `%LOCALAPPDATA%\HinoGateway` (Windows) or the platform local-app-data directory |
| Windows production/service | `%ProgramData%\HinoGateway` |
| Linux production/service | `/var/lib/hinogateway` |

The root contains `hino.json`, `hino.json.bak`, `hino.secrets.json`, `hino.retain.json`, `logs/`,
`audit/`, and `mqtt/spool/`. The root is protected for the service identity (plus SYSTEM and local
Administrators on Windows), and secret temporary files receive restrictive permissions before
their first content byte is written. Back up the whole root; `.hino` exports intentionally omit
secrets and retained runtime state.

Override with environment variables such as:

```text
Data__Root=D:\HinoGatewayData
Security__Enabled=true
Kestrel__Endpoints__Http__Url=http://127.0.0.1:2000
```

### Windows service

For a full field runbook — publishing, transferring to an industrial PC, service installation,
acceptance checklist, upgrade/rollback and troubleshooting — see
[`HUONG_DAN_TRIEN_KHAI_IPC.md`](HUONG_DAN_TRIEN_KHAI_IPC.md) (Vietnamese). The short version:

Publish to a fixed program directory, keep mutable state in ProgramData, then create the service
from an elevated PowerShell prompt:

```powershell
dotnet publish src/HinoGateway.Server -c Release -r win-x64 --self-contained false -o 'C:\Program Files\HinoGateway'
New-Service -Name HinoGateway -BinaryPathName '"C:\Program Files\HinoGateway\HinoGateway.Server.exe"' -StartupType Automatic
Start-Service HinoGateway
```

If the service runs under a dedicated account, grant that account **Full control** of the chosen
`Data:Root` before first start — the gateway rewrites that folder's ACL on startup to lock it to
itself, SYSTEM and local Administrators, and rewriting an ACL needs the "Change permissions" right
that Modify does not include. Modify alone is not an error: the gateway logs a warning, leaves the
inherited permissions in place and carries on. Do not grant it write access to the program directory.

### Linux systemd

```ini
[Unit]
Description=HinoGateway
After=network-online.target
Wants=network-online.target

[Service]
User=hinogateway
Group=hinogateway
WorkingDirectory=/opt/hinogateway
ExecStart=/opt/hinogateway/HinoGateway.Server
Environment=DOTNET_ENVIRONMENT=Production
Environment=Data__Root=/var/lib/hinogateway
Restart=on-failure
RestartSec=5
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
```

Create the state directory first with `install -d -m 0700 -o hinogateway -g hinogateway
/var/lib/hinogateway`. The application is systemd-aware and reports a fatal abandoned worker by
exiting for the service manager to restart it.

## Control safety and version 7 migration

Saving a connection, a folder or a tag applies it: edits made within a moment of each other are
coalesced into one engine reload, so there is no second "Apply to engine" step to forget. The IEC-104
and MQTT pages still apply on their own button, because restarting those links drops the connection
to the control centre.

Deleting takes the dependants with it — a connection's tags, the alarm rules watching them, the
IEC-104 points mapping them, an accumulator counting them — after a confirmation that states the
counts. Formulas and commands that read a deleted tag are reported rather than deleted, and have to
be corrected before the project applies again.

Every writable tag has an `AllowWrite` switch and optional engineering-unit min/max. It is off by
default. A Bool tag is written strictly: `true`, `false`, `1` or `0` and nothing else, so a stray `2`
cannot close a coil. IEC-104 also has a station-wide `AllowCommands` switch, per-point command type,
source CIDR allow-list, bounded command queue and deadline.

Commands are queued per device: one queue per physical connection, so their admission order towards
that device is preserved, and a device that has stopped answering delays only commands aimed at
itself. Admission capacity is global.

Project format version 7 changes old Single/Double IEC controls to select-before-operate (SBO).

Migration **does** set `AllowWrite` on tags that a version 6 file had already mapped as IEC command
points. That is not a new permission: in version 6, mapping a point as commandable *was* the
permission, and the two switches were only split apart in version 7. Dropping it on the way through
would silently make every control point read-only and get the file rejected by validation — a
restore from a `.hino` backup would fail with a message about a safety switch rather than about the
file's age.

Every tag changed this way is named in a warning, logged at load and returned in the `import`
response as `migrationNotes`. Read that list: it is the complete set of points that an upgraded
project can command, and the last chance to review them before the gateway starts accepting
commands for them. Before applying an upgraded project:

1. Export and archive the current project and data root.
2. Review every tag with `AllowWrite: true`, its write limits and its IEC mapping.
3. Confirm the control centre uses SELECT then EXECUTE within 30 seconds for Single/Double controls.
4. Apply while the plant has a safe control window and verify ACT_CON/ACT_TERM plus `audit/*.jsonl`.

The REST and IEC paths share the same bounded command dispatcher and durable audit result. If the
PLC write succeeds but audit persistence fails, the command remains `Successful`; the API reports
`auditPersisted: false`, and the component-health alarm identifies the audit fault.

## Commands

A command is a script that runs when a tag is written and writes on to the plant. It exists because
an IEC-104 setpoint lands in a memory tag — the control centre speaks in percent of the plant while
the PLC wants kilowatts in a particular register — and without something in between the number
arrives, reads back correctly on interrogation, and changes nothing in the field.

```
if (Tag("PLCControl/EnableEVNMode") != true) return;
Write("PLCControl/SettingPEVN", 165 * value / 100);
```

`value` is what was just written into the trigger tag. `var`, `if`/`else` and `return;` are
available; **there are no loops**, which is what makes "how long can a command run" arithmetic rather
than a halting problem. Tag paths must be string literals, so the gateway knows what a command reads
and writes before it runs — which is what lets it refuse a command naming a missing tag or one whose
`AllowWrite` switch is off.

Rules worth knowing, all of them deliberate:

- Every write goes through the same dispatcher as an operator's: write policy, per-device queue,
  deadline and audit trail, with the command named as the actor.
- A command runs on a **change it witnessed while running**. The value a trigger already held when
  the gateway started is not an order, so a restart never sends a setpoint by itself.
- A value that is not `Good` is not written, and a condition that cannot be decided stops the
  command. The failure is reported on the Commands page.
- Commands do not chain: a trigger tag written by another command is refused at load and logged.
- The Commands page has a dry run — the same code path, listing what would be written, writing
  nothing. Commission a curtailment command with it rather than with the control centre.

## Northbound links

IEC-104 is a controlled station: the control centre connects to the configured bind address/port.
Spontaneous reports use deadbands, quality transitions, a true point-rate limiter and multi-object
ASDUs (500 points/s by default; tune only against the commissioned control centre). Periodic refresh
is spread over its interval instead of emitted as one burst. Gateway and
lib60870 queues are bounded; source drops, coalescing and hard capacity drops are separate status
counters. Restrict `AllowedClients` and the host firewall because IEC-104 has no native peer
authentication. Use a VPN/TLS tunnel where required.

MQTT publishes JSON batches with QoS 0/1/2, TLS, credentials held in the secret store, exponential
reconnect and a segmented, checksummed disk journal. A segment is acknowledged only after the
broker accepts PUBLISH; rejected CONNACK/PUBACK results stay spooled. Queue and spool sizes are
bounded and visible on the MQTT page.

## Operations

- `/health/live` detects a runtime that requires process replacement.
- `/health/ready` fails for stopped, stale, recovering or crash-looping supervised components.
- `/api/health/components` exposes component heartbeat/failure/restart-storm detail; active items
  also appear on the Live Data page.
- Logs roll by day/size. Audit JSONL files are batch-fsynced and retained by `Audit` settings
  (90 days, 180 files and 2 GiB by default).
- Live alarm state and the local alarm journal are memory-only. The durable audit trail is not an
  alarm historian. After restart, alarm state is rebuilt from fresh tag values; use the upstream
  SCADA/MQTT consumer for long-term alarm history.
- Browser SignalR WebSockets carry their API key in the standard `access_token` query parameter
  because browser WebSocket handshakes cannot set the custom API-key header. Configure IIS/nginx to
  log the path without its query string (for nginx, prefer `$uri` over `$request_uri`), protect proxy
  logs like credentials and rotate the key if a query-bearing access log was exposed.

## Load and endurance tests

```powershell
dotnet run --project tests/HinoGateway.LoadTests -- quick
dotnet run --project tests/HinoGateway.LoadTests -- chaos
dotnet run --project tests/HinoGateway.LoadTests -- soak
```

`quick`, `mixed` and `slowlink` stress field polling against a separate slave-farm process.
`fullstack`, `chaos` and `soak` additionally exercise real TCP IEC-104, Kestrel/SignalR, REST,
alarms and the offline MQTT spool in the same component stack. This is not a black-box deployment
test of a separately installed Windows/systemd service. Reports include poll ratio, queue drops,
CPU/RSS/private bytes, managed heap/fragmentation, handles, GC counts and reload min/p95/max plus
managed-memory checkpoints. Run the 30-minute soak on the target IPC before release.

## Project layout

| Project | Role |
| --- | --- |
| `HinoGateway.Core` | Contracts, project format and durable-file primitives |
| `HinoGateway.Engine` | Device workers, tag store, internal tags and reload lifecycle |
| `HinoGateway.Drivers.*` | Simulator, Modbus TCP and Siemens S7 drivers |
| `HinoGateway.Northbound.*` | IEC-104 controlled station and MQTT publisher/spool |
| `HinoGateway.Storage` | JSON project, secrets and retentive values |
| `HinoGateway.Server` | Host, API, security, health, audit and supervisors |
| `HinoGateway.Web` | Browser UI — conventions in [`docs/UI_GUIDELINES.md`](docs/UI_GUIDELINES.md) |
| `tests/*` | Unit/recovery tests and load profiles |
