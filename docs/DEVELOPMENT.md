# Developing DashyNMS

Technical reference for building, running and understanding the codebase.
Looking for what the app *does*? See the [README](../README.md) instead.

The source is published for reference and security review; pull requests
aren't accepted. See the [LICENSE](../LICENSE), and [SECURITY.md](../SECURITY.md)
for reporting a security problem. Bugs and ideas are welcome as
[issues](https://github.com/DashyNMS/desktop/issues).

## Requirements

- Windows 10 1809 (build 17763) or later — earlier builds have no toast support.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build.
  `winget install Microsoft.DotNet.SDK.9`, or install Visual Studio with the
  **.NET desktop development** workload.
- A LibreNMS instance with the API enabled, and a token from
  **Settings, API, API Access**.
- A GitHub token with `read:packages`, for the `DashyNMS.Core` package (see
  [Core](#core) below).

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

### Core

The shared code (`DesktopNMS.Core`: the LibreNMS client, models, alerting, graph
styling, sign-in and more) lives in [DashyNMS/core](https://github.com/DashyNMS/core),
with its own tests and CI. DashyNMS Mobile and the MCP server use it too. This
app takes it as the **`DashyNMS.Core`** package from GitHub Packages. The
assembly and namespaces are still `DesktopNMS.Core`.

GitHub Packages needs a login even for public packages, so set
`DASHYNMS_PACKAGES_TOKEN` before restoring:

```powershell
gh auth refresh -s read:packages
$env:DASHYNMS_PACKAGES_TOKEN = gh auth token
```

The version is pinned in `src/DesktopNMS/DesktopNMS.csproj`
(`DashyNMSCoreVersion`). Between Core releases it's a development build of
core's `main`: every merge there publishes `x.y.z-ci.N`. Pin the one this app
needs, and move to a release before shipping.

To change Core and the app together, build against a local checkout of
DashyNMS/core instead of the package:

```powershell
$env:DASHYNMS_LOCAL_CORE = "C:\path\to\core"
dotnet build DesktopNMS.sln
```

Run Core's tests there (`dotnet test DashyNMS.Core.sln`), merge the Core change,
then pin this app to the development build it produces.

To produce a single self-contained `.exe`:

For an installable build with Intelligence's network tools, first publish
the MCP server (requires the .NET 10 SDK alongside .NET 9):

```powershell
./tools/Publish-Mcp.ps1 -Source ../mcp
```

Then publish the desktop app:

```bash
dotnet publish src/DesktopNMS/DesktopNMS.csproj -c Release -o publish
```

Release builds are configured for `win-x64`, self-contained and single-file, so
`publish/DashyNMS.exe` runs on a machine with no .NET installed.
The accompanying `publish/mcp` folder contains the self-contained network
tools, their licence and the source commit in `BUILD.txt`. Keep it beside the
app. The installer requires the MCP executable; preview and release workflows
fetch an exact MCP commit, build it first and verify the bundled files before
packaging. When updating the pinned commit in both workflows, use a commit
containing the licence's permission to bundle it with DashyNMS.

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

Upgrading is installing a newer setup over the top: the same AppId reuses
the install folder and the shortcut choices, and setup closes a running copy
through the Restart Manager. The in-app updater does exactly this, silently.

Uninstalling asks you to exit DashyNMS first if it's running (uninstall can't
close it the way setup can), removes the "start with Windows" Run value, and
runs `DashyNMS.exe --uninstall` to remove the notification registration. It
then asks whether to remove `%APPDATA%\DashyNMS` and `%LOCALAPPDATA%\DashyNMS`
too, defaulting to keep. A silent uninstall (`/VERYSILENT`) always keeps them.

## Layout

```
src/
  DesktopNMS/               WPF application (product name DashyNMS); Core is
                             the DashyNMS.Core package (see "Core" above)
    Views/ ViewModels/      MVVM, no code-behind logic beyond view concerns
    Services/               Session, polling, toasts, tray icon, windows
    Security/               DPAPI token, credential and chat storage
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
| `chats\` | Saved Intelligence chats, encrypted for the current Windows user and separated by LibreNMS server and ChatGPT account |
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

## Commits, pull requests and issues

Every change goes through a branch and a pull request into `master`, one issue
per PR, and is squash-merged. The PR title becomes the commit on `master`, so
the title is what the history reads like.

PR titles, commit messages and **issue titles** follow
[Conventional Commits](https://www.conventionalcommits.org/), in plain words.
The same standard is used in DashyNMS/core, mobile and mcp.

```
type(scope): what changes, as the user would notice it
```

- **type** is one of:

  | Type | For |
  |---|---|
  | `feat` | Something new a user can do or see |
  | `fix` | Something that was wrong now works |
  | `perf` | Faster or lighter, with no change in behaviour |
  | `refactor` | Code restructured, with no change in behaviour |
  | `docs` | README, docs, What's New |
  | `test` | Tests only |
  | `build` | Project files, packages, the installer, the version number |
  | `ci` | GitHub Actions workflows |
  | `chore` | Anything else that doesn't touch the app |
  | `revert` | Undoing an earlier change |

- **scope** is the area of the app, in lower case:
  - Screens: `dashboard`, `alerts`, `devices`, `device-details`, `logs`,
    `graylog`, `maps`, `health`, `neighbours`, `ports`, `wireless`, `apps`,
    `routing`, `graphs`, `rules`, `groups`, `maintenance`, `search`,
    `reports`, `catch-up`, `dependencies` (device dependencies)
  - Around the app: `settings`, `notifications`, `tray`, `startup`,
    `sign-in`, `updates`, `servers` (several LibreNMS servers), `users`,
    `billing`, `pollers`, `oxidized`, `diagnostics`, `accessibility`,
    `intelligence`, `mcp`, `kiosk`, `demo`
  - Code and packaging: `core`, `presentation`, `avalonia`, `api`,
    `installer`, `deps` (package updates)

  Separate two scopes with a comma (`fix(logs,dashboard): …`). Leave it out
  when a change has no single area (`docs: …`). A new area can add a scope;
  add it to this list in the same PR.
- **The description** says what changes, not how. Start with a lower-case
  letter, no full stop, and keep the whole title under about 72 characters.
  Write it as the result someone would notice, for example "neighbours load for
  ping-only devices", not "handle 404 in PortsApi".
- **No issue numbers in the title.** Put `Closes #NN` in the PR description
  instead. GitHub adds the PR number to the squash commit itself.
- **A breaking change** (to the settings format, or to Core's public API that
  DashyNMS Mobile uses) gets a `!` after the scope, `feat(core)!: …`, and a
  sentence in the description saying what breaks.

Examples:

```
fix(device-details): neighbours load for ping-only devices
fix(logs): device and type filters show "All devices" and "Any type"
feat(intelligence): choose how much detail answers give
refactor(intelligence): split the chat list and composer out of the view model
test(core): run the locked-file reset test on Windows only
build: bump the version to 2.0.0
ci: build the bundled MCP server in preview and release builds
docs: What's New for 2.0.0-preview.3
```

Commits on a branch use the same format. They disappear in the squash, so only
the PR title has to be right.

**Issue titles** use the same form, worded as the result the fixing PR will
deliver, so the PR can reuse the title:
`fix(intelligence): saving chats no longer stutters while answers stream`,
`feat(alerts): add a note without acknowledging`. An epic ends with
`(epic)`; an issue waiting on LibreNMS ends with `(blocked upstream)`, as its
label says. Labels carry the rest (`bug`, `LibreNMS 26.10`…), so titles don't
repeat them.

Commits, PRs and release notes carry no AI attribution or `Co-Authored-By`
lines.

The PR description starts with `Closes #NN`, then says briefly what changed
and how it was checked. Release notes and What's New are written separately,
for users.

## Roadmap

Tracked as [GitHub issues](https://github.com/DashyNMS/desktop/issues), with
one [milestone](https://github.com/DashyNMS/desktop/milestones) per release.
Larger pieces of work have an epic issue that lists its parts in order:

- [2.0.0](https://github.com/DashyNMS/desktop/milestone/3): one Avalonia app
  for Windows, macOS and Linux, plus Intelligence and LibreNMS 26.10 compatibility. The work is in eight batches,
  listed in the epic [#259](https://github.com/DashyNMS/desktop/issues/259).
- 2.1.0 to 2.4.0: features again, including Intelligence across the app
  ([#337](https://github.com/DashyNMS/desktop/issues/337)) and device
  dependencies ([#305](https://github.com/DashyNMS/desktop/issues/305)).
- [2.5.0](https://github.com/DashyNMS/desktop/milestone/9): multiple LibreNMS
  servers.

Issues are labelled by kind: `bug`, `Feature`, `enhancement`, `UI/UX`,
`Performance`, `accessibility`, `documentation`, `.NET` or `github_actions`.
