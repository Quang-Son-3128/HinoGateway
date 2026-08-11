# HinoGateway reliability update — 2026-08-11

This update changes operational defaults and should be commissioned, not copied blindly onto a
live controller.

- Persistent state now defaults to `%ProgramData%\HinoGateway` for a Windows production service and
  `/var/lib/hinogateway` on Linux. Development uses local application data. Copy the old data root
  before first start or explicitly set `Data:Root` to its existing absolute directory.
- Production no longer creates demo devices when `hino.json` is absent. An empty runtime is logged.
- Project format is version 7. Existing Single/Double IEC commands migrate to SBO. `AllowWrite`
  stays off unless it was explicitly enabled.
- MQTT disk spooling is a segmented checksummed journal. Legacy `.msg` entries are drained first and
  remain compatible.
- IEC-104 now limits gateway and library queues, packs compatible points into ASDUs, spreads periodic
  refresh and exposes hard capacity drops separately from coalescing/source drops.
- Control commands preserve FIFO per device. PLC outcome and audit persistence outcome are reported
  independently.
- Audit JSONL is batch-fsynced and has configurable retention. Secret temporary files are protected
  before contents are written.
- **Audit failure no longer blocks control.** A command that reached the device is reported as
  successful with `auditPersisted: false` and an `auditError`, and the `audit-trail` component goes
  unhealthy. This reverses the previous fail-closed behaviour: the truth about the device and the
  truth about the audit are now separate facts rather than one being reported as the other. Monitor
  free space on the data root, and treat an unhealthy `audit-trail` as an operational stop condition
  if your site rules require a complete trail (IEC 62443-3-3 SR 2.8).
- Readiness and the Live Data page expose stale loops, recovery grace and restart storms.
- Importing or loading a project older than version 7 keeps tags commanded over IEC-104 writable:
  in version 6 the command mapping *was* the authorisation, so both `RequireSelect` and `AllowWrite`
  are migrated together. Tags that were never commandable stay read-only and must be enabled by hand.
- On Windows the service account needs **Full control** of `Data:Root`, not Modify — the gateway
  rewrites that folder's ACL at startup. With Modify only, it logs a warning and runs with the
  inherited permissions instead of refusing to start.

Before rollout: back up the complete data root; validate/import the project on a staging instance;
review writable tags, limits, IEC CIDRs/SBO and MQTT credentials; run `quick` plus the 30-minute
`soak` profile on representative hardware; then verify `/health/ready`, component health, audit and
northbound drop counters during a controlled plant window.
