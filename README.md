# DashyNMS

A Windows desktop app for keeping an eye on your [LibreNMS](https://www.librenms.org/)
network from the tray, without living in a browser tab. Sign in once with an
API token and DashyNMS polls your server in the background, raises Windows
notifications when something changes, and gives you a full desktop UI for
devices, alerts, alert rules and graphs.

> Building or contributing to DashyNMS instead of using it? See
> [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Getting started

1. **Install.** Download the latest installer from
   [Releases](https://github.com/DashyNMS/desktop/releases) and run it - no
   admin rights needed, it installs just for your Windows account.
2. **Sign in.** Enter your LibreNMS server address and an API token (LibreNMS:
   **Settings → API → API Access**, or ask whoever manages your instance for
   one). The token is encrypted for your Windows account only and never
   leaves your machine except to talk to your own server.
3. **Leave it running.** Closing the window sends DashyNMS to the
   notification area rather than quitting it - it keeps polling and will
   notify you when something needs attention.

## What you can do with it

### Keep an eye on things

- **Dashboard.** A drag-and-resize grid of widgets you lay out yourself:
  live alerts, an alerts-at-a-glance gauge, device status counts, pinned
  sensor readings, a graph of any device's own metrics, a list of devices
  you've recently looked at, and your pinned devices.
- **Health.** dBm, signal strength, temperature and fan speed across every
  device in one place, colour-coded against whatever limits LibreNMS (or you,
  in Settings) has configured.
- **Notifications.** A Windows toast per alert, with independent control per
  severity over how insistent it is - fade away, stay until dismissed, or
  stay with a repeating sound. Quiet hours and start-up suppression keep it
  from being noisy on a machine you actually work on.
- **Tray icon.** Shows your worst outstanding severity and how many alerts
  are open, at a glance, without opening the window.

### Devices

- A sortable, filterable grid of every device - status, IP, OS, hardware,
  location, uptime, and more columns you can turn on from the column header.
- **Pin your important devices** to keep them at the top of the list
  regardless of sort or filter, and a **"Recently viewed" strip** that
  remembers what you've just been looking at.
- **Multi-select bulk actions** - pin, unpin, add to a group, or rediscover
  several devices at once, from the right-click menu.
- **Add a device** without leaving the app - SNMP v1/v2c/v3 or ping-only,
  with the same safety options (force-add, ping fallback) LibreNMS's own API
  offers.
- Click through to a full **Device View** for anything: status, hardware,
  active alerts and uptime history; sensors grouped by component; ports with
  LLDP/CDP neighbour discovery; CPU/memory/disk; VLANs, the MAC and ARP
  tables; graphs of any of the device's own metrics; and a searchable event
  log. From there you can also rediscover the device on demand, schedule a
  maintenance window, open it in your browser/Telnet/SSH client, or edit or
  delete it.

### Alerts and rules

- The **Alerts** list filters by severity and state with one click, searches
  across host/rule/note, and exports the current view to CSV.
- **Acknowledge** an alert with an optional note, or put it back to active -
  both go through LibreNMS's own API.
- The **Rules** tab is a full alert-rule editor that mirrors LibreNMS's own:
  build conditions with nested AND/OR groups, import from another rule or a
  pasted SQL query, target specific devices/groups/locations (or everything),
  and see at a glance how many alerts each rule currently has raised - click
  through to see exactly which ones.
- **Alert templates** - list, create and edit the templates that control what
  a notification actually says, and see which rules each one drives.

### Groups and locations

Browse and filter by LibreNMS's device groups and locations the same way you
would on the website, with the same click-through into Device View.

## Settings

Everything is reachable from one Settings window: connection details and
polling interval, alert display and health thresholds, per-severity
notification behaviour, device-list defaults, window/start-up behaviour, and
appearance (dark or light theme, accent colour, and your server's own logo if
it has one).

## Where your data lives

Everything DashyNMS stores lives under `%APPDATA%\DashyNMS` on your machine -
your server address and settings, the encrypted API token, and a rolling log
file kept for 14 days. Nothing is sent anywhere except to the LibreNMS server
you signed in to. Signing out, or deleting that folder, removes it all.

## Requirements

Windows 10 (build 17763) or later, and a LibreNMS instance with the API
enabled.

## Feedback and roadmap

Found a bug, or want to see something added? [Open an issue](https://github.com/DashyNMS/desktop/issues).
Planned work is tracked there too, grouped under the
[1.1.0](https://github.com/DashyNMS/desktop/milestone/2) and
[1.2.0](https://github.com/DashyNMS/desktop/milestone/3) milestones.

Want to contribute code? See [CONTRIBUTING.md](CONTRIBUTING.md).
