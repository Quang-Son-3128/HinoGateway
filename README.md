# HinoGateway

A cross-platform, web-based industrial gateway / SCADA runtime — the successor to the Windows-only
NanoX Gateway. The server is headless ASP.NET Core (runs on Windows **and** Linux/Docker); the UI is
a Blazor WebAssembly app that runs in any browser, on any OS.

> **Status: Phase 0 (POC).** Proves the end-to-end vertical slice — driver → engine → realtime → web —
> works and is cross-platform. Not production yet.

## Architecture

```
Browser (any OS)  ──►  HinoGateway.Server (ASP.NET Core, :2000)
  Blazor WASM UI        ├─ REST  /api/*          (config + snapshots)
                        ├─ SignalR /hubs/telemetry (realtime tag push)
                        └─ DeviceEngine (background)
                             ├─ DeviceWorker per device (isolated, supervised)
                             └─ drivers: Simulator (+ Modbus/S7/… later)
```

### Projects

| Project | Role |
|---|---|
| `HinoGateway.Core` | Contracts & models: `TagValue` (value + **quality** + **source timestamp**), `IDeviceDriver`, `DeviceConfig`. No dependencies. |
| `HinoGateway.Engine` | `DeviceEngine`, `DeviceWorker` (one isolated worker per device), `TagStore` (latest state + bounded update channel with backpressure). |
| `HinoGateway.Drivers.Simulator` | Reference `IDeviceDriver` — fabricates waveforms (`sine`/`ramp`/`random`/`bool`) to exercise the engine without hardware. |
| `HinoGateway.Server` | Hosts the WASM app, REST API, SignalR hub, and runs the engine. |
| `HinoGateway.Web` | Blazor WebAssembly UI (live tag grid). |
| `HinoGateway.Engine.Tests` | xUnit tests: value production + fault isolation. |

### Design principles (connectivity layer)

- **Accuracy** — every value carries `Quality` (Good/Uncertain/Bad) and the timestamp it was sampled
  *at the device*. A faulted device's tags are marked **Bad** immediately, never left stale.
- **Stability** — each device runs in its own supervised worker; a slow/broken device cannot stall
  or crash others. Faults trigger exponential backoff + reconnect.
- **Performance** — async non-blocking I/O; engine-owned deterministic scan scheduling; a bounded
  `DropOldest` update channel so a slow UI/network never backs up the poll loops.

## Run

Requires the **.NET 8 SDK** (pinned via `global.json`).

```bash
dotnet run --project src/HinoGateway.Server --urls http://localhost:2000
```

Open <http://localhost:2000> — you'll see the live tag grid (values, quality, source time).

### Tests

```bash
dotnet test
```

### Docker (cross-platform)

```bash
docker compose up --build
```

Then open <http://localhost:2000>. This is the proof the whole stack runs on Linux with no Windows
dependency.

## Roadmap

- **Phase 0 (done)** — driver → engine → SignalR → web dashboard, on Linux/Docker.
- **Phase 1 (done)** — configuration as a single JSON project file, REST config CRUD + web
  Connections/Tag Database pages, runtime engine reload (no restart), `.hino` import/export.
- **Phase 2 (done)** — alarms: limits/digital/quality conditions with hysteresis and on/off delays,
  acknowledgement, a local event trail, and the Alarms + Alarm Rules pages.
- **Phase 3 (in progress)** — northbound. IEC-104 controlled station is done (see below); MQTT
  publishing of tag values and alarm events with store-and-forward is still to come.
- **Phase 4** — more drivers (Modbus RTU, S7, IEC104), scripting, `.nanox` importer.

## Northbound: IEC 60870-5-104 (link to the utility)

The gateway can act as an IEC-104 **controlled station**: it listens (default 2404) and the utility's
control centre connects in. Configure it at `/iec104`, or through `/api/iec104`.

- **Signal list** — one row per point: tag, information object address (IOA), ASDU type
  (`M_SP_NA_1`, `M_DP_NA_1`, `M_ME_NA_1`, `M_ME_NB_1`, `M_ME_NC_1`, `M_IT_NA_1`), an optional
  report-by-exception deadband, a report scale, and whether the centre may command the point
  (`C_SC_NA_1`, `C_DC_NA_1`, `C_SE_NA_1/NB_1/NC_1`). Import/export it as CSV — the utility hands the
  list over as a spreadsheet.
- **Reporting** — general interrogation answers with every mapped point (including ones never read,
  flagged invalid); changes go out spontaneously, honouring the deadband, and **a change of quality is
  always reported** even when the number is unchanged. Optional cyclic transmission.
- **Commands** are confirmed (ACT_CON), executed, then terminated (ACT_TERM) — or refused negatively.
  They are written through the same path as a local write, so validation, inverse scaling and range
  checks all apply. Select/execute is honoured with a 30 s selection window.
- **Cyclic transmission** — `PeriodicIntervalSeconds` sends every mapped point on a fixed cycle
  (COT 1) on top of the spontaneous reports. Report-by-exception alone cannot promise a refresh
  interval, so this is what satisfies a "must be updated at least every N minutes" requirement.
- **Clock sync** (`C_CS_NA_1`) is accepted and the offset recorded, but the machine clock is *not*
  moved — that belongs to NTP, and jumping a gateway's clock invalidates timestamps it already sent.
- **Reset process** (`C_RP_NA_1`) is confirmed and clears what the station has already reported, so
  the next interrogation and every change are transmitted afresh. It deliberately does not touch the
  plant: in IEC 60870-5 the command resets the telecontrol station, and a gateway that restarted
  inverters because of a protocol housekeeping command would be dangerous.

### EVN 378/QĐ-EVN (rooftop solar, Decree 58)

The mandatory signal set maps onto the configuration like this — note that the decision asks for
**T13 = `M_ME_NC_1`**, i.e. measurands *without* a time tag (turn "Time tag" off; leaving it on makes
them `M_ME_TF_1`), while T30 status points *do* carry one:

| Signal | Decision | Type here | IOA |
| --- | --- | --- | --- |
| P-out injected to the grid | T13 | ShortFloat, no time tag | 1 |
| Total inverter output (Pinv-out) | T13 | ShortFloat, no time tag | 2 |
| Previous day's energy (Ainv_D-1) | T13 | ShortFloat, no time tag | 3 |
| Enable P-out control | T45 | SinglePoint + SingleCommand | 11 |
| Setpoint P-out in % | T50 | ShortFloat + SetpointShort | 12 |
| Setpoint P-out in kW | T50 | ShortFloat + SetpointShort | 13 |

Set `PeriodicIntervalSeconds = 300` for the required five-minute refresh. Still missing for full
compliance: local 5-minute storage kept for 7 days, backfill after a comms outage, TLS/VPN on the
link, the daily Ainv_D-1 calculation, and distributing a received setpoint across the inverters.
- Set `BindAddress` to `0.0.0.0` only when the control centre is on another machine; there is no
  authentication in the protocol itself, so restrict it at the firewall.

**A controlled station never dials out.** The control centre connects to it, so reconnecting after an
outage is the control centre's job — this gateway closes its connections cleanly on shutdown and
accepts new ones as soon as it is back. If a client does not come back, check its own reconnect
setting first.

**Watch out for a second station on the same port.** Binding a specific address (127.0.0.1) succeeds
even when another program already listens on `0.0.0.0` for the same port, and then a client that
connects while this gateway is down lands on *that* station, stays connected to it and never returns.
The symptom looks exactly like a broken reconnect. The status panel raises a warning when it detects
another listener on the port — stop the other program, or give this gateway a port of its own.

Known deviation: answers to a **counter interrogation** carry cause of transmission 20 instead of the
standard's 37. lib60870.NET 2.3.0 overwrites it (confirmed at byte level, and it cannot be set from
outside the library); the type, addresses and values are correct. Worth confirming during
commissioning if the control centre polls counters that way.

## Modbus addressing

| Syntax | Meaning |
| --- | --- |
| `40001` / `30001` / `10001` / `00001` | classic 5-digit: holding / input register, discrete input, coil |
| `HR:0` `IR:10` `CO:3` `DI:1` | prefixed, 0-based (the colon is optional) |
| `12` | bare number = holding register, 0-based |
| `HR:10.3` / `40011.3` | bit 3 of a register (0 = least significant). Read-only — writing one bit would mean read-modify-write and race the PLC for the other 15 |

Types: `Bool`, `Int16/32/64`, `UInt16/32/64`, `Float32/64`. For multi-register values, `WordSwap`
selects register order and `ByteSwap` the byte order inside each register — the two are independent,
which covers all four layouts devices use in practice.

Reads are grouped into block requests: points are merged while the gap between them stays under
`MaxRegisterGap` (default 8) / `MaxBitGap` (default 64), never exceeding the protocol's 125
registers / 2000 bits. If a device refuses a block (illegal data address), it is split in half and
retried until the offending point is alone, then that point alone is skipped for `QuarantineMs`
(default 60 s) — so one bad address costs one tag, not the whole area. `PollDeadlineMs`
(default 5000) bounds a whole scan so retries and timeouts cannot silently stretch it.
`GET /api/diagnostics/blocks` shows the live plan: range, tag count, latency, errors, quarantine.

## Why there is no database

This is a **gateway**, not a SCADA server. It is installed on an industrial PC, acquires data and
forwards it upstream; the server owns history. So the gateway keeps:

- **configuration** in one JSON file (`hino.json`, the same `.hino` envelope as export/import) —
  read into memory at startup and rewritten atomically on change, with a `.bak` of the previous
  version. Deployment is a folder copy; recovery is restoring a file an engineer can read and diff.
  It sits **next to the executable** (`AppContext.BaseDirectory`), deliberately not the working
  directory or content root: those differ between `dotnet run` and running the built binary, and the
  result is two configuration files quietly disagreeing about what the gateway is doing. In
  development that means `bin/Debug/net8.0/hino.json` — keep a `.hino` export if you clean the build
  output. Set `Config:Path` to an absolute path to put it somewhere else.
- **live tag values and alarm state** in memory.
- **the alarm event trail** in a bounded in-memory ring (`Alarms:JournalCapacity`, default 2000) —
  enough to answer "what just happened?" on the box itself.

No database engine to install, no native libraries per CPU architecture, no schema migration to get
wrong in the field. Set `Config:Path` to put the project file somewhere else.
