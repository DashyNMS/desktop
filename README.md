# DashyNMS

A Windows desktop client for [LibreNMS](https://www.librenms.org/). Signs in with
an API token, keeps an eye on your alerts from the notification area, and raises
Windows notifications when something changes — with per-severity control over how
insistent those notifications are.

This is the first slice of what is intended to grow into a broader LibreNMS
desktop client, so the API layer is built to have devices, ports, services and
the rest bolted on without rework.

## What it does today

- **API token sign-in.** Address plus token, validated against `/api/v0/system`
  before the session is accepted. The token is stored with DPAPI, encrypted for
  your Windows account on this machine only, and can be turned off entirely.
- **Dashboard with drag/resize widgets.** A snapped grid you lay out yourself:
  Sensors (pinned dBm, signal, temperature or fan readings), Alerts (a live
  filtered feed), Alerts gauge (critical/warning/acknowledged at a glance) and
  Device status (up/down/maintenance/disabled counts). Dragging or resizing a
  widget over another pushes it out of the way instead of blocking.
- **Health tab.** Signal, temperature and fan speed sections across every
  device, with configurable warning/critical thresholds.
- **Alert list with filtering.** Filter chips for Critical / Warning / OK and for
  Active / Acknowledged / Recovered, plus a free-text search across host, rule,
  note and alert id. Your chip selection is remembered between runs.
- **Device list.** Status, IP, OS, hardware, location and uptime for every
  device, with the same hostname/sysName naming preference as the alert list,
  and a one-click jump to that device's alerts.
- **Acknowledge from the app.** Acknowledge with an optional note, or return an
  acknowledged alert to active. Both go through the real LibreNMS endpoints.
- **One shared poller per resource.** Sensors, devices and alerts are each
  polled once on a common schedule and fanned out to whichever tabs are open,
  rather than every tab polling independently.
- **Windows notifications with configurable stickiness.** Per severity, choose
  between a normal toast that fades, one that stays on screen until dismissed,
  or one that stays with a looping alarm tone. Critical defaults to sticky,
  warning to transient.
- **Runs in the notification area.** Closing the window keeps it polling. The
  tray icon shows the worst outstanding severity and the count.
- **Quiet hours, burst collapsing and start-up suppression** so it is usable on a
  machine you actually work on.
- **Your connected server's own branding.** The shell header shows your
  LibreNMS instance's own logo, if it has one, rather than a generic mark.

## Requirements

- Windows 10 1809 (build 17763) or later — earlier builds have no toast support.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build.
  `winget install Microsoft.DotNet.SDK.9`, or install Visual Studio with the
  **.NET desktop development** workload.
- A LibreNMS instance with the API enabled, and a token from
  **Settings, API, API Access**.

## Building and running

The solution and project files are still named `DesktopNMS.*` internally (an
implementation detail with no user visibility); the product itself — the
window title, tray icon, installed exe, Start Menu entry — is DashyNMS.

```bash
dotnet build DesktopNMS.sln
```

```bash
dotnet run --project src/DesktopNMS/DesktopNMS.csproj
```

To produce a single self-contained `.exe`:

```bash
dotnet publish src/DesktopNMS/DesktopNMS.csproj -c Release -o publish
```

Release builds are configured for `win-x64`, self-contained and single-file, so
`publish/DashyNMS.exe` runs on a machine with no .NET installed.

### Building the installer

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install JRSoftware.InnoSetup`). With a Release build already published
to `publish/` (see above):

```bash
"C:/Users/<you>/AppData/Local/Programs/Inno Setup 6/ISCC.exe" /DAppVersion=0.3.0 installer/DashyNMS.iss
```

This produces `dist/DashyNMS-Setup-<version>.exe`. The installer runs
per-user with no admin/UAC prompt — matching the app itself, which keeps all
its state under the current Windows account — into
`%LocalAppData%\Programs\DashyNMS`. The wizard offers a Start Menu shortcut
(on by default) and a desktop shortcut (off by default) as independent
checkboxes. For a silent install with the same defaults:

```bash
dist/DashyNMS-Setup-0.3.0.exe /VERYSILENT /SUPPRESSMSGBOXES /TASKS=startmenuicon
```

Add `,desktopicon` to `/TASKS` to also create a desktop shortcut. Note that
unticking the Start Menu shortcut only skips creating it during setup — the
app recreates a minimal one for itself the first time it sends a Windows
notification, since Windows requires an app to have one before it will show
up in Settings > Notifications (see `ToastIdentity.cs`).

## Layout

```
src/
  DesktopNMS.Core/          No UI. Reusable against any front end.
    Api/                    Transport, resource clients, connection details
    Models/                 Alert, Device, AlertRule, SystemInfo
    Json/                   Converters for LibreNMS's stringly-typed JSON
    Alerting/               Poll-to-poll change detection
    Configuration/          Settings, paths, notification state
    Security/               DPAPI token storage
  DesktopNMS/               WPF application (product name DashyNMS)
    Views/ ViewModels/      MVVM, no code-behind logic beyond view concerns
    Services/               Session, polling, toasts, tray icon, windows
    Infrastructure/         Commands, observable base, file logging
    Themes/                 Dark theme resource dictionary
tools/
  New-AppIcon.ps1           Regenerates Assets/app.ico
installer/
  DashyNMS.iss              Inno Setup script; see "Building the installer" above
```

### Why the API layer looks like this

`ILibreNmsClient` groups endpoints by resource (`client.Alerts`,
`client.Devices`, `client.Rules`, `client.System`). Adding ports or services
means a new interface, a new implementation over the same `ILibreNmsTransport`,
and one new property — no changes to anything that already works.

Two LibreNMS quirks are handled centrally rather than at each call site:

- Rows come straight out of MySQL, so integers and booleans arrive as JSON
  strings (`"1"`) and timestamps as `2024-05-01 09:31:07` rather than ISO-8601.
  The converters in `Json/LibreNmsJson.cs` absorb all of that.
- Every model carries `[JsonExtensionData]`, so a LibreNMS upgrade that adds
  columns will not break deserialisation.

## Where things are stored

Everything lives under `%APPDATA%\DashyNMS`:

| File | Contents |
| --- | --- |
| `settings.json` | Server address, polling, notification and window settings |
| `token.dat` | The API token, DPAPI-encrypted for your Windows account |
| `notified.json` | Alert ids already announced, so a restart is quiet |
| `logs\dashynms-<date>.log` | Rolling log, kept for 14 days |

Delete `token.dat` to force a fresh sign-in. Delete the folder to reset
completely.

> The app was originally built and shipped as **DesktopNMS**; this folder, the
> exe name and every user-visible string were renamed to DashyNMS afterwards.
> The one thing deliberately left untouched is the DPAPI encryption salt for
> `token.dat` — it is an invisible cryptographic detail, and changing it would
> have invalidated every already-saved token, forcing a re-sign-in for a purely
> cosmetic rename.

## LibreNMS endpoints used

| Purpose | Call |
| --- | --- |
| Validate credentials | `GET /api/v0/system` |
| Fetch alerts | `GET /api/v0/alerts?state=1,2&order=timestamp desc` |
| Fetch devices | `GET /api/v0/devices` |
| Fetch alert rules | `GET /api/v0/rules` / `GET /api/v0/rules/{id}` |
| Fetch alert log (fault detail) | `GET /api/v0/logs/alertlog/{device}` |
| Acknowledge | `PUT /api/v0/alerts/{id}` with `{"note":"...","until_clear":true}` |
| Return to active | `PUT /api/v0/alerts/unmute/{id}` |

Severity comes from the alert *rule*, not the alert, so DashyNMS fetches all
severities and filters client-side; that also makes the filter chips instant.
Recovered alerts (`state=0`) are not fetched by default because LibreNMS keeps a
row per rule and device indefinitely — ticking the Recovered chip turns the
wider fetch on for you.

## Notes on Windows notifications

Toast "stickiness" maps onto Windows toast scenarios: *stay until dismissed* is
the `Reminder` scenario, *stay with a repeating sound* is `Alarm`. Both require
the toast to carry at least one button, which DashyNMS always adds, and both
are honoured only from Windows 10 1809 onwards. If the toast system is
unavailable for any reason, the app falls back to notification-area balloons
rather than going silent.

The first time a toast is shown, the notification library registers a COM
activator for DashyNMS, and the app itself creates a Start Menu shortcut (see
`ToastIdentity.cs`) so Windows has a durable identity to hang the notification
off — without one, Windows will still display a toast on some builds but never
lists the app in Settings > Notifications. That is how an unpackaged desktop
app is allowed to raise toasts and receive clicks on them, and it is why toast
buttons still work when the app is sitting in the tray.

## Roadmap

The obvious next slices, roughly in order of usefulness:

1. Alert history and per-rule drill-down (`/api/v0/rules`, already modelled).
2. Ports and traffic graphs.
3. Multiple LibreNMS instances in one window.
4. Alert rule editing.
