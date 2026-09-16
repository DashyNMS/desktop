# One-off script: recreates ROADMAP.md as GitHub milestones/labels/issues.
# Not meant to be re-run - kept here only as a record of how it was done.

$repo = "DashyNMS/desktop"
$gh = "C:\Program Files\GitHub CLI\gh.exe"

# --- Labels -----------------------------------------------------------

$labels = @(
    @{ Name = "Feature"; Color = "1D76DB"; Description = "New capability or LibreNMS API surface being exposed" }
    @{ Name = "UI/UX"; Color = "8250DF"; Description = "Usability, layout, or interaction improvement" }
    @{ Name = "Performance"; Color = "FBCA04"; Description = "Under-the-hood efficiency or responsiveness work" }
)

foreach ($label in $labels) {
    & $gh label create $label.Name -R $repo --color $label.Color --description $label.Description --force
}

# --- Milestones ---------------------------------------------------------

$milestones = @(
    @{ Title = "1.0.0"; Description = "Performance Graphs & Rendering Foundations - Alerting Depth - Device & Inventory Management" }
    @{ Title = "1.1.0"; Description = "Administration - Topology & Extended Monitoring - Production Hardening" }
    @{ Title = "1.2.0"; Description = "Multi-Instance" }
)

foreach ($milestone in $milestones) {
    & $gh api "repos/$repo/milestones" -f "title=$($milestone.Title)" -f "description=$($milestone.Description)" | Out-Null
}

# --- Issues ---------------------------------------------------------------
# Category -> label name: Feature, UI/UX, Performance (Bug is unused here -
# the roadmap has none - but already exists on the repo for future issues).

$issues = @(
    # ===================== 1.0.0 =====================
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "Feature"; Title = "Port traffic graphs on the Ports tab"; Body = "Per-port historical graphs (in/out bps, errors, utilization) on the Ports tab." }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "Feature"; Title = "Sensor history graphs on the Sensors tab"; Body = "Historical graphs for dBm, temperature, fan, and voltage/current/power sensors." }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "Feature"; Title = "Processor/memory/storage trend graphs on Resources"; Body = "Historical trend graphs built on LibreNMS's processor/mempool/storage data." }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "Feature"; Title = "Device Overview mini/sparkline graphs"; Body = "An at-a-glance sparkline summary on the Device Overview tab." }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "Feature"; Title = "Fleet-wide health graph widget on the Dashboard"; Body = "A Dashboard widget showing fleet-wide health history." }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "Feature"; Title = "Shared time-range picker for graphs"; Body = "1h/1d/1w/1m/1y plus custom, reused across every graph rather than each building its own." }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "Feature"; Title = "Decide graph rendering approach: rendered PNG vs. native charting"; Body = "LibreNMS's own rendered-PNG graph endpoints vs. pulling raw RRD data and charting natively. Native is preferred - matches the dark theme and supports hover/zoom." }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "UI/UX"; Title = "Shared sortable/resizable/persisted DataGrid pattern"; Body = 'Establish one shared DataGrid pattern: sortable, resizable, reorderable columns, with layout persisted in `settings.json`. Currently every grid (Devices, Alerts, Ports, VLANs, FDB, ARP, Sensors) is a plain fixed-width DataGrid with nothing persisted.' }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "UI/UX"; Title = "Roll out the 3-state loading pattern everywhere"; Body = "Extend the loading/empty/no-matches pattern already used by VLANs/FDB/ARP to every remaining section." }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "UI/UX"; Title = "Apply loading/empty/error treatment to the new graphs"; Body = "Match the same 3-state pattern used elsewhere once graphs land." }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "Performance"; Title = "Batch large ObservableCollection updates instead of Clear()+Add() loops"; Body = 'Worst offenders are the FDB/ARP tables and alert list in `DeviceDetailViewModel.cs` / `MainViewModel.cs`.' }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "Performance"; Title = "Add cancellation for DeviceDetailViewModel's in-flight section loads"; Body = 'A `CancellationTokenSource` so closing or switching a device window cancels its in-flight section loads (Ports/VLANs/FDB/ARP/Sensors/Resources/Graphs) instead of letting them complete unused.' }
    @{ M = "1.0.0"; T = "Performance Graphs & Rendering Foundations"; C = "Performance"; Title = "Cache fetched graph data per time-range for the window's lifetime"; Body = "Avoid re-fetching on every tab re-entry within the same device window session." }

    @{ M = "1.0.0"; T = "Alerting Depth"; C = "Feature"; Title = "Alert rule create/edit/delete"; Body = "Full CRUD for alert rules from inside the app." }
    @{ M = "1.0.0"; T = "Alerting Depth"; C = "Feature"; Title = "Rule builder UI"; Body = "Mirrors LibreNMS's own condition/macro/severity/device-and-group targeting." }
    @{ M = "1.0.0"; T = "Alerting Depth"; C = "Feature"; Title = "Alert templates: list/create/edit"; Body = "Manage alert templates from the app." }
    @{ M = "1.0.0"; T = "Alerting Depth"; C = "Feature"; Title = "Notification transport management"; Body = "Email/Slack/Teams/webhook/etc., scoped to whatever the connected server's API version exposes." }
    @{ M = "1.0.0"; T = "Alerting Depth"; C = "Feature"; Title = "Rule testing / match preview"; Body = 'A preview of which devices a rule would match, shown before saving it.' }
    @{ M = "1.0.0"; T = "Alerting Depth"; C = "Feature"; Title = "Delete an alert outright"; Body = "If distinct from acknowledge in the target API version." }
    @{ M = "1.0.0"; T = "Alerting Depth"; C = "UI/UX"; Title = "Confirmation prompt for large bulk ack/unack"; Body = "Currently fires immediately regardless of selection size." }
    @{ M = "1.0.0"; T = "Alerting Depth"; C = "UI/UX"; Title = "CSV export / copy-to-clipboard for alerts and logs"; Body = "The alert list and a device's alert/event log." }
    @{ M = "1.0.0"; T = "Alerting Depth"; C = "UI/UX"; Title = "Search/filter on the rule list"; Body = "Mirrors the existing Devices/Alerts search pattern." }
    @{ M = "1.0.0"; T = "Alerting Depth"; C = "Performance"; Title = "Add backoff/retry-with-jitter to LibreNmsTransport"; Body = "No protection currently exists against a client hammering a struggling server on the normal poll interval." }
    @{ M = "1.0.0"; T = "Alerting Depth"; C = "Performance"; Title = "Fetch rules/templates through the shared-poller pattern"; Body = "Rather than ad hoc per-view calls." }

    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "Feature"; Title = "Add device"; Body = "Hostname, SNMP/community, transport options." }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "Feature"; Title = "Edit device"; Body = "Display name, overrides, poller group." }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "Feature"; Title = "Delete device, with confirmation"; Body = "" }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "Feature"; Title = "Rediscover / re-poll a device on demand"; Body = "" }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "Feature"; Title = "Locations: list/add/edit/delete"; Body = "" }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "Feature"; Title = "Device groups: full CRUD"; Body = "Today only membership is shown, not group management itself." }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "Feature"; Title = "Services: list/add/edit/delete per device"; Body = "" }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "Feature"; Title = "Maintenance windows: create/cancel"; Body = 'Today `IsUnderMaintenanceAsync` is a read-only check with no way to schedule or cancel one from the app.' }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "UI/UX"; Title = "Multi-select + bulk actions on the Devices grid"; Body = 'Delete, add to group, schedule maintenance - mirrors what the Alerts grid already has via `SelectionMode="Extended"`.' }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "UI/UX"; Title = "Column show/hide/reorder + persistence on the Devices grid"; Body = "First real rollout of the shared grid pattern from this release's graphs work." }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "UI/UX"; Title = "Pin/favorite devices"; Body = "Surfaced at the top of the Devices list and/or a Dashboard widget." }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "UI/UX"; Title = "Recently-viewed devices list"; Body = "" }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "UI/UX"; Title = "Add/Edit device forms with inline validation"; Body = "Matching LibreNMS's own field constraints." }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "Performance"; Title = "Fix the N+1 pattern in DeviceMonitor.RefreshMaintenanceIdsAsync"; Body = "One API call per device today - look for a bulk alternative or a longer-lived cache." }
    @{ M = "1.0.0"; T = "Device & Inventory Management"; C = "Performance"; Title = "Cache device-group membership with a longer TTL"; Body = '`GetMembershipByDeviceAsync` - membership changes far less often than device state, so it doesn''t need the main poll interval''s cadence.' }

    # ===================== 1.1.0 =====================
    @{ M = "1.1.0"; T = "Administration"; C = "Feature"; Title = "User management"; Body = "List/add/edit/delete, permission-gated." }
    @{ M = "1.1.0"; T = "Administration"; C = "Feature"; Title = "Billing module"; Body = "Bills, bill history/data - confirm real user value before building." }
    @{ M = "1.1.0"; T = "Administration"; C = "Feature"; Title = "Oxidized config-backup integration"; Body = "Config diffs/history, if the connected server exposes it." }
    @{ M = "1.1.0"; T = "Administration"; C = "Feature"; Title = "Distributed poller status view"; Body = "" }
    @{ M = "1.1.0"; T = "Administration"; C = "Feature"; Title = "Instance-scoped settings data model"; Body = "Extend the settings model to be instance-scoped in preparation for 1.2.0, without yet exposing multi-instance UI." }
    @{ M = "1.1.0"; T = "Administration"; C = "UI/UX"; Title = "Follow Windows theme option"; Body = "Alongside the existing manual Dark/Light toggle." }
    @{ M = "1.1.0"; T = "Administration"; C = "UI/UX"; Title = "Detect non-admin tokens via a permissions probe"; Body = "Hide/disable admin-only sections gracefully rather than surfacing failed calls." }
    @{ M = "1.1.0"; T = "Administration"; C = "Performance"; Title = "Throttle polling for background/minimized state"; Body = '`DeviceMonitor`, `SensorMonitor`, and `AlertMonitor` currently all poll at full rate regardless of visibility - more important once 1.2.0 can multiply pollers per instance.' }

    @{ M = "1.1.0"; T = "Topology & Extended Monitoring"; C = "Feature"; Title = "Routing protocol data"; Body = "BGP neighbors/sessions, OSPF neighbors, VRF listings." }
    @{ M = "1.1.0"; T = "Topology & Extended Monitoring"; C = "Feature"; Title = "PoE port status"; Body = "Power over Ethernet." }
    @{ M = "1.1.0"; T = "Topology & Extended Monitoring"; C = "Feature"; Title = "Wireless sensor data"; Body = "Signal, noise, client counts, where the server models it." }
    @{ M = "1.1.0"; T = "Topology & Extended Monitoring"; C = "Feature"; Title = "Topology/map view"; Body = "Built from the Links (LLDP/CDP) data already being fetched." }
    @{ M = "1.1.0"; T = "Topology & Extended Monitoring"; C = "Feature"; Title = "Fleet-wide search"; Body = "Across devices/ports/alerts/sensors from a single box." }
    @{ M = "1.1.0"; T = "Topology & Extended Monitoring"; C = "UI/UX"; Title = "Breadcrumb/back-navigation between device-detail windows"; Body = "Today's Hyperlinks (Location, Device Groups, neighbours) only jump forward, with no history." }
    @{ M = "1.1.0"; T = "Topology & Extended Monitoring"; C = "UI/UX"; Title = "Remember window size/position/state per window type"; Body = "Device View, Settings, etc. - instead of always reopening at a hardcoded size." }
    @{ M = "1.1.0"; T = "Topology & Extended Monitoring"; C = "Performance"; Title = "Lazy-load topology/map data and debounce search"; Body = "No broader performance work is specific to this theme." }

    @{ M = "1.1.0"; T = "Production Hardening"; C = "Feature"; Title = "Auto-update mechanism"; Body = "Check for a new release, download/install." }
    @{ M = "1.1.0"; T = "Production Hardening"; C = "Feature"; Title = "Opt-in crash/error reporting"; Body = "" }
    @{ M = "1.1.0"; T = "Production Hardening"; C = "Feature"; Title = "Full documentation pass"; Body = "README, in-app help." }
    @{ M = "1.1.0"; T = "Production Hardening"; C = "Feature"; Title = "Installer polish"; Body = "Code signing if feasible, upgrade-in-place testing." }
    @{ M = "1.1.0"; T = "Production Hardening"; C = "UI/UX"; Title = "Keyboard-shortcuts help overlay"; Body = "F5/Ctrl+A/Ctrl+L/Ctrl+F exist today but are undiscoverable." }
    @{ M = "1.1.0"; T = "Production Hardening"; C = "UI/UX"; Title = "Audit confirmation-dialog consistency"; Body = "Standardize usage across every destructive/impactful action." }
    @{ M = "1.1.0"; T = "Production Hardening"; C = "UI/UX"; Title = "CSV export parity across remaining tables"; Body = "Ports, FDB, ARP, VLANs, Sensors." }
    @{ M = "1.1.0"; T = "Production Hardening"; C = "UI/UX"; Title = "Accessibility pass"; Body = "Screen-reader labels, high-contrast support, keyboard-only navigation." }
    @{ M = "1.1.0"; T = "Production Hardening"; C = "Performance"; Title = "Disk-persisted last-known-good cache"; Body = "So a cold start against an unreachable server shows stale-but-useful data instead of a blank app." }
    @{ M = "1.1.0"; T = "Production Hardening"; C = "Performance"; Title = "Final pass on batched collection updates"; Body = "Confirm every large collection update in the app is batched, not item-by-item." }
    @{ M = "1.1.0"; T = "Production Hardening"; C = "Performance"; Title = "Load-test against a large simulated fleet"; Body = "Hundreds of devices - profile UI responsiveness." }

    # ===================== 1.2.0 =====================
    @{ M = "1.2.0"; T = "Multi-Instance"; C = "Feature"; Title = "Connect to and switch between multiple LibreNMS instances"; Body = "" }
    @{ M = "1.2.0"; T = "Multi-Instance"; C = "Feature"; Title = "Per-instance credential/session storage"; Body = "Extending the existing DPAPI token model." }
    @{ M = "1.2.0"; T = "Multi-Instance"; C = "Feature"; Title = "Instance switcher in the shell header"; Body = "Alongside or replacing the current single server-branding logo." }
    @{ M = "1.2.0"; T = "Multi-Instance"; C = "Feature"; Title = "Per-instance settings"; Body = "Poll interval, thresholds, notification behavior, with sensible shared defaults." }
    @{ M = "1.2.0"; T = "Multi-Instance"; C = "Feature"; Title = "Disambiguate which instance an alert/notification came from"; Body = "Tray icon and notifications." }
    @{ M = "1.2.0"; T = "Multi-Instance"; C = "Feature"; Title = "Decide independent windows vs. unified cross-instance view"; Body = 'Fully independent per-instance windows vs. a unified cross-instance Devices/Alerts view - changes `DeviceListViewModel`''s architecture significantly and should be settled before implementation starts.' }
    @{ M = "1.2.0"; T = "Multi-Instance"; C = "UI/UX"; Title = "Clear visual indicator of the active instance"; Body = "Everywhere it matters: title bar, tray tooltip, device windows." }
    @{ M = "1.2.0"; T = "Multi-Instance"; C = "Performance"; Title = "Bound per-instance poller growth"; Body = "Ensure per-instance pollers don't compound linearly without bound - reuse the throttling work from 1.1.0's Administration milestone." }
)

$count = 0
foreach ($issue in $issues) {
    $body = "$($issue.Body)`n`nPart of the **$($issue.T)** theme for $($issue.M)."
    & $gh issue create -R $repo `
        --title $issue.Title `
        --body $body `
        --label $issue.C `
        --milestone $issue.M
    $count++
    Write-Host "[$count/$($issues.Count)] $($issue.Title)"
}

Write-Host "Done: $count issues created."
