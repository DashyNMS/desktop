# DashyNMS Roadmap

The plan from here to a 1.0.0 production release and beyond. This file is the
living source of truth for scope and sequencing — update it as work lands or
priorities shift, rather than re-deriving the plan from chat history each
time. Check off items as they ship; leave a note next to anything that gets
cut, deferred, or changed in scope so the reasoning survives.

Each milestone groups its scope under three headings:

- **Feature** — new LibreNMS API surface being exposed
- **UI/UX** — usability work identified by auditing the app as it exists today
- **Performance** — under-the-hood work identified the same way

## Overview

| Version | Themes |
| --- | --- |
| [1.0.0](#100) | Performance Graphs & Rendering Foundations · Alerting Depth · Device & Inventory Management |
| [1.1.0](#110) | Administration · Topology & Extended Monitoring · Production Hardening |
| [1.2.0](#120) | Multi-Instance |

---

## 1.0.0

### Performance Graphs & Rendering Foundations

The single biggest gap versus the LibreNMS web UI: there is currently no
historical charting anywhere in the app — Ports, Sensors, and Resources are
all live-snapshot only.

**Feature**
- [ ] Port traffic graphs (in/out bps, errors, utilization) on the Ports tab, per port
- [ ] Sensor history graphs (dBm, temperature, fan, voltage/current/power) on the Sensors tab
- [ ] Processor/memory/storage trend graphs on the Resources tab
- [ ] Device Overview mini/sparkline graphs for an at-a-glance summary
- [ ] Fleet-wide health graph widget on the Dashboard
- [ ] Shared time-range picker (1h/1d/1w/1m/1y, custom) reused across every graph
- [ ] Decide rendering approach: LibreNMS's own rendered-PNG graph endpoints vs. pulling raw RRD data and charting natively (native preferred — matches dark theme, supports hover/zoom)

**UI/UX**
- [ ] Establish one shared DataGrid pattern: sortable, resizable, reorderable columns, with layout persisted in `settings.json` — currently every grid (Devices, Alerts, Ports, VLANs, FDB, ARP, Sensors) is a plain fixed-width `DataGrid` with nothing persisted
- [ ] Roll the existing 3-state loading pattern (loading / empty / no-matches, already used by VLANs/FDB/ARP) out to every remaining section
- [ ] Apply the same loading/empty/error treatment to the new graphs

**Performance**
- [ ] Replace `Clear()` + item-by-item `Add()` on large collections with a batched reset — worst offenders are the FDB/ARP tables and alert list in `DeviceDetailViewModel.cs` / `MainViewModel.cs`
- [ ] Add a `CancellationTokenSource` to `DeviceDetailViewModel` so closing or switching a device window cancels its in-flight section loads (Ports/VLANs/FDB/ARP/Sensors/Resources/Graphs) instead of letting them complete unused
- [ ] Cache fetched graph data per time-range for the life of the window, to avoid re-fetching on every tab re-entry

### Alerting Depth

Alerts today are watch/acknowledge/unmute/rules-list only — there is no way
to configure what gets alerted on from inside the app.

**Feature**
- [ ] Alert rule create/edit/delete
- [ ] Rule builder UI mirroring LibreNMS's own condition/macro/severity/device-and-group targeting
- [ ] Alert templates: list/create/edit
- [ ] Notification transport management (email/Slack/Teams/webhook/etc.), scoped to whatever the connected server's API version exposes
- [ ] Rule testing / "which devices would this match" preview before saving
- [ ] Delete an alert outright, if distinct from acknowledge in the target API version

**UI/UX**
- [ ] Confirmation prompt before a bulk acknowledge/unacknowledge above a size threshold — it currently fires immediately regardless of selection size
- [ ] CSV export / copy-to-clipboard for the alert list and a device's alert/event log
- [ ] Search/filter on the rule list, mirroring the existing Devices/Alerts search pattern

**Performance**
- [ ] Add backoff/retry-with-jitter to `LibreNmsTransport` for sustained failures — there is currently no protection against a client hammering a struggling server on the normal poll interval
- [ ] Fetch rules/templates through the existing shared-poller pattern rather than ad hoc per-view calls

### Device & Inventory Management

Manage the fleet, not just view it.

**Feature**
- [ ] Add device (hostname, SNMP/community, transport options)
- [ ] Edit device (display name, overrides, poller group)
- [ ] Delete device, with confirmation
- [ ] Rediscover / re-poll a device on demand
- [ ] Locations: list/add/edit/delete
- [ ] Device groups: full CRUD (today only membership is shown, not group management itself)
- [ ] Services: list/add/edit/delete per device
- [ ] Maintenance windows: create/cancel — today `IsUnderMaintenanceAsync` is a read-only check with no way to schedule or cancel one from the app

**UI/UX**
- [ ] Multi-select + bulk actions on the Devices grid (delete, add to group, schedule maintenance) — mirrors what the Alerts grid already has via `SelectionMode="Extended"`
- [ ] Column show/hide/reorder + persistence on the Devices grid — first real rollout of the shared grid pattern from this release's graphs work
- [ ] Pin/favorite devices, surfaced at the top of the Devices list and/or a Dashboard widget
- [ ] Recently-viewed devices list
- [ ] Add/Edit device forms with inline validation matching LibreNMS's own field constraints

**Performance**
- [ ] Address the acknowledged N+1 pattern in `DeviceMonitor.RefreshMaintenanceIdsAsync` (one API call per device) — look for a bulk alternative or a longer-lived cache
- [ ] Cache device-group membership (`GetMembershipByDeviceAsync`) with a longer TTL than the main poll interval, since membership changes far less often than device state

---

## 1.1.0

### Administration

**Feature**
- [ ] User management (list/add/edit/delete), permission-gated
- [ ] Billing module (bills, bill history/data) — confirm real user value before building
- [ ] Oxidized config-backup integration (config diffs/history), if the connected server exposes it
- [ ] Distributed poller status view
- [ ] Settings data model extended to be instance-scoped in preparation for 1.2.0, without yet exposing multi-instance UI

**UI/UX**
- [ ] "Follow Windows theme" option alongside the existing manual Dark/Light toggle
- [ ] Admin-only sections detect a non-admin token via a permissions probe and hide/disable gracefully, rather than surfacing failed calls

**Performance**
- [ ] Throttle or pause polling for background tabs and when the window is minimized to tray — `DeviceMonitor`, `SensorMonitor`, and `AlertMonitor` currently all poll at full rate regardless of visibility, and this becomes more important once 1.2.0 can multiply pollers per instance

### Topology & Extended Monitoring

**Feature**
- [ ] Routing protocol data: BGP neighbors/sessions, OSPF neighbors, VRF listings
- [ ] PoE (Power over Ethernet) port status
- [ ] Wireless sensor data (signal, noise, client counts) where the server models it
- [ ] Topology/map view built from the Links (LLDP/CDP) data already being fetched
- [ ] Fleet-wide search across devices/ports/alerts/sensors from a single box

**UI/UX**
- [ ] Breadcrumb/back-navigation between device-detail windows — today's Hyperlinks (Location, Device Groups, neighbours) only jump forward, with no history
- [ ] Window size/position/state remembered per window type (Device View, Settings, etc.) instead of always reopening at a hardcoded size

**Performance**
- [ ] Lazy-load topology/map data and debounce the new search input; no broader perf work is specific to this theme

### Production Hardening

Not new API surface — stability and polish for a public 1.1.0.

**Feature**
- [ ] Auto-update mechanism (check for a new release, download/install)
- [ ] Opt-in crash/error reporting
- [ ] Full documentation pass (README, in-app help)
- [ ] Installer polish (code signing if feasible, upgrade-in-place testing)

**UI/UX**
- [ ] Keyboard-shortcuts help overlay — F5/Ctrl+A/Ctrl+L/Ctrl+F exist today but are undiscoverable
- [ ] Audit and standardize confirmation-dialog usage across every destructive/impactful action
- [ ] CSV export parity across every remaining table (Ports, FDB, ARP, VLANs, Sensors)
- [ ] Accessibility pass: screen-reader labels, high-contrast support, keyboard-only navigation

**Performance**
- [ ] Disk-persisted last-known-good cache so a cold start against an unreachable server shows stale-but-useful data instead of a blank app
- [ ] Final pass confirming every large collection update in the app is batched, not item-by-item
- [ ] Load-test against a large simulated fleet (hundreds of devices) and profile UI responsiveness

---

## 1.2.0

### Multi-Instance

**Feature**
- [ ] Connect to and switch between multiple LibreNMS instances/profiles in one running app
- [ ] Per-instance credential/session storage, extending the existing DPAPI token model
- [ ] Instance switcher in the shell header, alongside or replacing the current single server-branding logo
- [ ] Per-instance settings (poll interval, thresholds, notification behavior) with sensible shared defaults
- [ ] Tray icon and notifications disambiguate which instance an alert came from
- [ ] Decide up front: fully independent per-instance windows vs. a unified cross-instance Devices/Alerts view — this changes `DeviceListViewModel`'s architecture significantly and should be settled before implementation starts

**UI/UX**
- [ ] Clear visual indicator of which instance is active everywhere it matters: title bar, tray tooltip, device windows

**Performance**
- [ ] Ensure per-instance pollers don't compound linearly without bound — reuse the throttling work from 1.1.0's Administration milestone
