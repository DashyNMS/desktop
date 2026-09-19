# Developing DashyNMS

Technical reference for building, running and understanding the codebase.
Looking for what the app *does*? See the [README](../README.md) instead.

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

```bash
dotnet test tests/DesktopNMS.Core.Tests/DesktopNMS.Core.Tests.csproj
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
"C:/Users/<you>/AppData/Local/Programs/Inno Setup 6/ISCC.exe" /DAppVersion=0.4.0 installer/DashyNMS.iss
```

This produces `dist/DashyNMS-Setup-<version>.exe`. The installer runs
per-user with no admin/UAC prompt — matching the app itself, which keeps all
its state under the current Windows account — into
`%LocalAppData%\Programs\DashyNMS`. The wizard offers a Start Menu shortcut
(on by default) and a desktop shortcut (off by default) as independent
checkboxes. For a silent install with the same defaults:

```bash
dist/DashyNMS-Setup-0.4.0.exe /VERYSILENT /SUPPRESSMSGBOXES /TASKS=startmenuicon
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
    Alerting/               Poll-to-poll change detection, the rule builder's
                             field catalog, SQL import/format for rule conditions
    Configuration/          Settings, paths, notification state
    Security/               DPAPI token storage
  DesktopNMS/               WPF application (product name DashyNMS)
    Views/ ViewModels/      MVVM, no code-behind logic beyond view concerns
    Services/               Session, polling, toasts, tray icon, windows
    Infrastructure/         Commands, observable base, file logging
    Themes/                 Dark/light theme resource dictionaries
tools/
  New-AppIcon.ps1           Regenerates Assets/app.ico
installer/
  DashyNMS.iss              Inno Setup script; see "Building the installer" above
```

### Why the API layer looks like this

`ILibreNmsClient` groups endpoints by resource (`client.Alerts`,
`client.Devices`, `client.Rules`, `client.System`). Adding a new resource
means a new interface, a new implementation over the same
`ILibreNmsTransport`, and one new property — no changes to anything that
already works.

Two LibreNMS quirks are handled centrally rather than at each call site:

- Rows come straight out of MySQL, so integers and booleans arrive as JSON
  strings (`"1"`) and timestamps as `2024-05-01 09:31:07` rather than ISO-8601.
  The converters in `Json/LibreNmsJson.cs` absorb all of that, including
  every timestamp being treated as the server's own wall-clock time —
  DashyNMS does no timezone conversion in either direction.
- Every model carries `[JsonExtensionData]`, so a LibreNMS upgrade that adds
  columns will not break deserialisation.

When a feature needs to write to LibreNMS, check its actual PHP source
(`includes/html/api_functions.inc.php` and the matching web form/controller
on [github.com/librenms/librenms](https://github.com/librenms/librenms)) for
what a field's *absence* means, not just its presence — several real bugs in
the rule editor came from the docs not saying that an omitted `extra` field
gets reset rather than preserved.

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
| Fetch/write alert rules | `GET/POST/PUT/DELETE /api/v0/rules(/{id})` |
| Fetch/write alert templates | `GET/POST /api/v0/alert_templates` |
| Fetch alert log (fault detail) | `GET /api/v0/logs/alertlog/{device}` |
| Fetch event log | `GET /api/v0/logs/eventlog/{device}` |
| Acknowledge | `PUT /api/v0/alerts/{id}` with `{"note":"...","until_clear":true}` |
| Return to active | `PUT /api/v0/alerts/unmute/{id}` |
| Fetch sensors (fleet-wide) | `GET /api/v0/resources/sensors` |
| Fetch ports for a device | `GET /api/v0/devices/{id}/ports?columns=...` |
| Fetch neighbours (LLDP/CDP) | `GET /api/v0/devices/{id}/links` |
| Fetch IP addresses | `GET /api/v0/devices/{id}/ip` |
| Fetch CPU/memory/disk | `GET /api/v0/devices/{id}/health/{processor,mempool,storage}(/{id})` |
| Fetch availability | `GET /api/v0/devices/{id}/availability` |
| Fetch outage history | `GET /api/v0/devices/{id}/outages` |
| Render a graph | `GET /api/v0/devices/{id}/graphs/{graph}/{from}/{to}` |
| Maintenance status / schedule | `GET/POST /api/v0/devices/{id}/maintenance` |

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

Tracked as [GitHub issues](https://github.com/DashyNMS/desktop/issues),
grouped under the [1.1.0](https://github.com/DashyNMS/desktop/milestone/2) and
[1.2.0](https://github.com/DashyNMS/desktop/milestone/3) milestones, each
issue labelled `Feature`, `UI/UX`, `Performance`, or `bug`.
