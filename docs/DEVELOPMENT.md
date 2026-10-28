# Developing DashyNMS

Technical reference for building, running and understanding the codebase.
Looking for what the app *does*? See the [README](../README.md) instead.

The source is published for reference and security review; pull requests
aren't accepted. See the [LICENSE](../LICENSE), and [SECURITY.md](../SECURITY.md)
for reporting a security problem. Bugs and ideas are welcome as
[issues](https://github.com/DashyNMS/desktop/issues).

## Requirements

- Windows 10 1809 (build 17763) or later, macOS 13 or later, or Linux with a desktop session.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build.
  `winget install Microsoft.DotNet.SDK.9`, or install Visual Studio with the
  **.NET desktop development** workload on Windows. Install the .NET SDK directly on macOS or Linux.
- A LibreNMS instance with the API enabled, and a token from
  **Settings, API, API Access**.
- A GitHub token with `read:packages`, for the `DashyNMS.Core` package (see
  [Core](#core) below).

## Building and running

### LibreNMS web sign-in

The Avalonia app uses `Avalonia.Controls.WebView` with Edge WebView2 on Windows,
WKWebView on macOS and GTK/WebKitGTK on Linux. Windows 10 may need the WebView2
Runtime installed. Linux web sign-in needs GTK 3, WebKitGTK 4.1 and libsoup 3
(`libgtk-3-0 libwebkit2gtk-4.1-0 libsoup-3.0-0` on Debian/Ubuntu).
Manual API-token entry remains available when a native browser cannot be used.

Each attempt uses a private/non-persistent browser session. Scripts run only on
the configured LibreNMS origin, and the existing Core flow handles token creation,
page reloads and accounts without API access. The browser uses OS certificate
trust: install a private CA in the OS trust store, or use manual token entry for
a server whose certificate the browser cannot validate. API certificate exceptions
never disable the browser's TLS checks. Passwords, cookies and tokens are not logged.

`dotnet run --project tools/WebSignInSmoke` checks the real native browser with
synthetic local HTML. CI runs it on all three operating systems (under Xvfb on
Linux), independently of the headless UI and shared-flow unit tests.

The solution and project files are still named `DesktopNMS.*` internally (an
implementation detail with no user visibility); the product itself — the
window title, tray icon, installed exe, Start Menu entry — is DashyNMS.

```bash
dotnet build DesktopNMS.sln
```


The Avalonia app is the desktop UI on Windows, macOS and Linux. The former
WPF project has been retired; its history remains in Git.

```bash
dotnet run --project src/DesktopNMS.Avalonia/DesktopNMS.Avalonia.csproj
```

### Development builds

After a pull request successfully merges into `master`, the **Development build**
Actions run produces Windows x64 Setup, macOS Intel/Apple Silicon DMGs, and Linux
x64/ARM64 AppImages. It builds the merged commit and runs the same package
verification and Windows upgrade checks used for previews. Closing an unmerged
PR does not build installers; PR creation and updates do not trigger this workflow.
Download the appropriate `package-<runtime>` artifact within seven days.
No tags or GitHub releases are created. Builds use a unique
`<version>-preview.pr.<PR>.<run>.<attempt>` version. They do not use distribution
signing credentials or notarisation; macOS uses local ad-hoc signatures.
Native packages and Native network tools keep their existing triggers and behavior,
including the 30-day retention for Native packages artifacts.

For a local portable Windows build, publish the pinned MCP source first
(requires .NET 10), then:

```powershell
./tools/Publish-Dev.ps1
```

This runs the Presentation and headless Avalonia UI input tests and publishes to a new versioned folder beneath
`artifacts/dev`. Previous builds remain available. For cross-platform installers,
see [Native packages](#native-packages).

Windows release and preview builds use the existing `%APPDATA%/DashyNMS`
profile from 1.x, including its DPAPI-encrypted secrets. Debug and `-dev.*`
builds retain the isolated `avalonia` subfolder. macOS and Linux keep their
existing Avalonia profile path. Each profile has one background instance;
launching it again brings the existing window forward.

### Error and usage sharing

The first normal launch asks for separate error and usage choices. Both start
off. Closing the prompt leaves the choice undecided and asks again on the next
launch. An explicit decline is saved. Settings, About can change either choice.
Demo and screenshot modes never send reports.

The reporter uses a bounded, sanitised local queue and a small Sentry envelope
adapter. It does not initialise the Sentry SDK, session tracking, tracing,
native crash handlers or log collection. Fatal managed errors are queued for
the next launch. Usage records one `dashynms.app_start` Application Metrics counter per process after consent,
without a user or installation identifier. See [PRIVACY.md](../PRIVACY.md) for
the payload and withdrawal behaviour.

Launches use `trace_metric` envelope items; managed errors retain `event` items.
Existing queued usage reports are converted on delivery without changing their
original timestamps. The queue and independent consent choices are unchanged.
The adapter supplies only allowlisted typed attributes and a random per-report
trace context, with no automatic tracing or HTTP instrumentation. IP and
user-agent inference are disabled for metrics. Application Metrics has its own
quota (currently 5 GB included on all plans, then $0.50/GB via pay-as-you-go);
check the project's availability, retention and budget before enabling it.

For release verification, check a consented launch appears under Explore →
Metrics as `dashynms.app_start` with value 1 and the expected release/platform.
Check both a fresh launch and a report queued six days earlier. Confirm accepted
metric outcomes and that neither launch appears in Errors; then send a controlled
managed error and confirm it still appears there. HTTP success alone does not
prove indexing. Metric timestamp acceptance depends on project retention; it
must cover the seven-day local queue window.

Managed errors include only approved application assemblies. Frames retain method
names and available IL offsets; async/iterator state-machine frames resolve to
the original method. Repository-relative `src/<project>/*.cs` names and positive
line numbers are included when symbols exist. Absolute paths, source text, local
variables and exception messages remain excluded. Unrecognised source paths are
omitted, and older queued frames without these optional fields still upload.
Release builds embed portable PDBs, including both desktop packaging scripts;
embedded symbols do not require separate PDB files beside a single-file app.
Build paths are mapped to a fixed root and stripped before reporting. Optimised
code and dependencies without shipped symbols can still lack line information;
Core package symbols are determined by that package's build. IL offsets refer to
the physical compiled method even when the display name is resolved from a
state machine. No Sentry source integration or source context is enabled here.

Expanded usage consent accepts `AppSettings.DiagnosticsConsentVersion` 1 or 2.
Version 0 and unknown versions never authorise broader metrics. Version 2 adds
session health, eligible only when both error and usage sharing are explicitly
selected. Existing version-1 metrics continue without sessions; legacy launch
consent continues with launches only. The updated prompt starts usage unchecked,
preserving the existing error choice. Explicit declines do not trigger a new
prompt. Closing the prompt leaves the old scope unchanged. Settings shows the
old scope; an explicit usage off/on choice accepts the current disclosure.
Disabling usage withdraws launch, broader metrics and session consent; disabling
errors withdraws sessions while leaving separately allowed usage metrics active.

`IAppMetrics.Record(MetricObservation)` accepts enum names and dimensions only.
No string tags, HTTP instrumentation, machine metadata or content are accepted.
The catalogue reserves counters for feature use, update outcomes and UI stalls;
distributions for startup, screen loads, API calls, Intelligence response/first
response and stall durations (milliseconds); and gauges for working set and
managed heap (bytes). Names use `dashynms.*`; each point has release, OS,
architecture and fixed `mode`, `area` and `outcome` attributes. Individual instrumentation is introduced separately; startup timing is
described below.

Counters are summed per built-in series, capped at 1,000,000 per buffer.
Durations retain the first and every tenth observation per series. Memory gauges
retain each five-minute sample within the shared capacity limits.
The buffer and sampling catalogue each hold at most 32 entries. A one-minute
background timer persists a batch into the existing ten-report / 32 KiB /
seven-day queue and attempts delivery. Normal exit persists the tail; abrupt
termination can lose it. Metrics uploads are paced to one request per minute.
Errors have independent backoff; global server limits pause both categories.
Withdrawal clears the buffer, stops the timer, cancels delivery and purges the
affected reports. Launch-only consent also purges broader queued metrics.
Capacity/expiry drops and at-least-once retries mean counts are approximate;
sampled durations are not exact population percentiles. Do not scale sampled
gauges as counts or infer users/retention from these measurements.

Startup timing (`dashynms.startup.duration`, milliseconds) starts at the first
Program static field initialisation, before the reporter and Avalonia setup.
It is a managed-entry proxy for process startup: OS process creation and CLR
loading before that point are excluded. Interactive readiness is the first
main or sign-in window's `Opened` callback followed by dispatcher `Loaded`
priority (after layout/render, before input). Subsequent shows and tray restores
do not generate a second observation. Hidden startup completes at the first
`Loaded` dispatcher turn after the main window and tray are configured and is
labelled `mode:background`; visible/sign-in startup uses `mode:interactive`.

Initial session restoration, including network and certificate confirmation,
is explicitly paused out of the monotonic duration. Screen/network data loading
is not a startup completion condition. Time uses `TimeProvider.GetTimestamp` /
`GetElapsedTime`, never wall-clock subtraction. A random 10% of eligible starts
is retained; durations over five minutes are discarded to bound debugger/sleep
outliers. Consent must already cover metrics at startup; accepting the prompt
cannot retrospectively report this launch. Demo/screenshot modes are excluded.

In Sentry Explore → Metrics, select `dashynms.startup.duration`, filter by
`sentry.release`, `operating_system` and `architecture`, and split by `mode`.
Use `p50(value,dashynms.startup.duration,distribution,millisecond)` for the median
and `p95(value,dashynms.startup.duration,distribution,millisecond)` for slow-case
startup. Never combine background and interactive starts into one percentile.
Small sampled populations and bounded delivery make these estimates, not
complete installation-wide measurements.

Screen loading (`dashynms.screen_load.duration`, milliseconds) measures primary
data readiness, not pixel-render time or every optional panel. Dashboard and
Health begin on `OnShown` after connection and complete after the next sensor
result has been applied; an already-loaded primary dataset completes as
`outcome:cached`. Alerts begin on a connected tab visit (including the initial
connected tab) and finish after the alert list is applied. Device Details begin
at view-model construction and finish when cached overview identity is ready,
or after a device poll applies that identity. Graphs, configuration backups,
inventory and other secondary panels do not extend this primary boundary.

Leaving a pending tab, closing Device Details or disconnecting completes as
`cancelled`. Primary-data failures are `failed`, and remote success is `success`.
Only an active visit can complete: overlapping starts cannot reset the timer,
and duplicate/background poll callbacks cannot create observations. A new
visible visit is eligible again. Durations above five minutes are discarded.
Only fixed `area` values (dashboard, health, alerts, devicedetails) and outcomes
are sent; no device IDs, titles, filters or data are attached. The shared sink
samples each series (first and every tenth), buffers and sends asynchronously.
Disk persistence runs outside the collection lock so UI collection never waits
on a disk flush. Snapshots that cannot be persisted, or whose queue consent
generation changes before persistence, are dropped.
Consent must be active at the start and retain the same generation at completion;
withdrawal/reconsent cannot revive a pending observation. Query percentiles split
by `area` and `outcome`, keeping cached and remote durations separate.

API performance uses Core's optional `LibreNmsTransport.RequestObserver`, attached
only while expanded metrics consent is active. Core captures monotonic elapsed
time once around a public transport call, including transient/stale-connection
retries, failover and response parsing. Collection deserialisation shares that
boundary and cannot emit a duplicate request observation. Temporary connection
probe transports do not inherit this observer. Core observer failures are caught
without changing the returned response or original exception.

Each logical request increments `dashynms.api.requests` (counter); elapsed time
also enters `dashynms.api.duration` (millisecond distribution, first and every
tenth observation per series). Fixed areas are `devicelist` (GET devices),
`alerts` (GET alerts), `sensors` (GET resources/sensors) and `apiother` (all other
routes/writes). Query parameters never become dimensions. Outcomes are success,
clienterror (HTTP 4xx), servererror (HTTP 5xx), timeout, networkfailure, cancelled
and failed (other API/parse failures). Cancellation is not treated as failure.
The full status code, URL, host, port, IDs, credentials, headers, body and
exception text are never passed to Desktop's metrics adapter.

Observer delegates capture a consent generation. Collection checks it under
the metrics buffer lock, so an old in-flight request cannot report after
withdrawal/reconsent. Error consent remains independent. Counts aggregate before
batching; duration sampling does not sample the counters, although the shared
capacity/expiry/retry limitations still affect interpretation. Use summed request
counters by area/outcome for volumes and failure ratios, and duration percentiles
by area/outcome for latency; do not use distribution sample counts as request
totals or count retries as separate requests.

UI responsiveness uses a one-second worker timer and at most one pending normal-
priority dispatcher callback. Queue-to-acknowledgement delay of at least 500 ms
increments `dashynms.ui_stall.count` and records a sampled millisecond distribution
in `dashynms.ui_stall.duration`. This is a dispatcher-latency proxy, not a native
hang detector or per-frame measurement. Counts aggregate; durations use the
shared first/every-tenth sampling and the existing queue/transmission limits.

Timing is monotonic. Each worker tick checks both monotonic and UTC elapsed time
locally: a worker gap over three seconds, or a discrepancy over one second,
invalidates a pending observation. Acknowledgement repeats the gap check so UI
resumption before the worker does not report sleep as a stall. Background
activity changes invalidate pending observations, and acknowledgement checks the
actual current visible/non-minimised window state on the UI thread. Initial
activity is refreshed before its periodic timer starts. Debugger attachment
excludes the entire interval, not just known breakpoints. Withdrawal stops the
probe timer; the shared consent generation rejects old callbacks after reconsent.
Shutdown disposes the timer and activity subscription before metrics are flushed.

A blocked UI never accumulates heartbeat callbacks: one remains queued until it
runs, even across exclusion or consent changes. No frames, screenshots, input,
window titles or customer content are inspected or sent. Conservative exclusion
can undercount real stalls during whole-process pauses or worker starvation;
the metric should not be treated as an exact crash/freeze count. Unit tests use
a manual clock/dispatcher to verify long stalls, bounded scheduling, sampling,
withdrawal, background/debugger exclusions and sleep/clock jumps without sleeps.

Feature use increments `dashynms.feature.use` under the existing expanded
consent. One use means a transition to a different Maps tab (network,
geographical or custom, all combined as `area:maps`), a transition into
Intelligence, entering dashboard edit mode from non-edit mode, or each built-in
widget successfully added through the picker. Re-selecting the active tab,
startup layout restoration, cancelling the picker, leaving edit mode, and
background callbacks do not count. Duplicate/template operations outside the
picker are not included in the widget-add measurement.

Widget categories are an explicit whitelist: Sensors, Alerts, AlertsGauge,
DeviceStatus, RecentlyViewed, PinnedDevices, Graph, Wireless, TopInterfaces,
TopErrors, TopDevices, EventLog and Graylog. Their fixed areas are the lowercase
`widget`-prefixed enum names (for example `widgetsensors`); unknown/future types
are skipped until deliberately added to the catalogue. No widget title,
dashboard name, custom type text, object ID, device detail, search term or map
location enters the adapter. The previous tab is used only to reject a repeated
selection and is not stored or transmitted as a transition history.

Counts are combined in the bounded local buffer and emitted as summaries, not
one outbound event per click. Feature counters are not sampled: timing sampling
does not change them. They still represent only opted-in activity, with possible
capacity/counter-cap drops, seven-day offline expiry and retry duplicates. Do
not interpret them as unique users, installation totals, journeys or retention.

Intelligence measurement wraps one `SendAsync` / logical `AskAsync` question,
including token renewal, internal provider retries and tool rounds. Explicitly
retrying a question starts a new logical request. Timing starts immediately
before scheduling the conversation work, after local question/composer setup.
First response means the first non-whitespace answer delta or completed answer
text applied to the view model on the UI thread; tool activity/status/image
callbacks are not first text. Completion is after the logical answer/outcome is
known, before final local chat persistence. A failed/cancelled request with no
text has no first-response point; a partial answer retains its first-text time
with the final failure/cancellation outcome.

`dashynms.intelligence.requests` counts each completion once. The millisecond
distributions `dashynms.intelligence.duration` and
`dashynms.intelligence.first_response` use the shared first/every-tenth sampling.
Only `area:intelligence` and fixed outcomes are added: success, cancelled,
timeout, authfailure, planlimit, modelfailure, toolfailure, networkfailure or
failed (including an empty answer). Categories use exception types and Core's
bounded failure enum; messages are never parsed into dimensions. Caller
cancellation and a provider timeout are distinguished by the request token.
Metrics consent must already be active when the question starts, and the same
generation must remain valid when it completes. Use counters for reliability
ratios; distribution sample counts are not request totals.

The timing helper accepts no question/answer text, chat/Spotlight objects, tool
arguments/results, provider/model names, accounts, credentials, token usage or
cost data. The existing UI callback only checks whether text is non-empty before
notifying the timer. No transcript tracing or additional tool-call records are
introduced. The normal bounded buffer/expiry/retry caveats apply.

Update reporting uses `dashynms.update.outcome` counters and sampled
`dashynms.update.duration` millisecond distributions. Fixed areas distinguish
check, availability, download/preparation, installation launch and target startup.
Availability and target-startup outcomes are count-only. Version dimensions
`update.source` / `update.target` strip a leading v and build metadata and accept
only validated version strings up to 32 characters; otherwise they are unknown.
`update.channel` is stable/preview. Check/availability channel describes the
selected policy; download/install channel describes the selected target release.
Platform/release remain the shared context. These dimensions participate in
aggregation keys so different targets/channels never merge.

Mode is the configured automatic/manual/notification policy for checks and
availability, and the actual manual/automatic path for package preparation.
Package-manager/unsupported installation flows report availability with
notification mode and do not invent downloads/installations. Turning automatic
downloads off still cancels automatic preparation; an explicit Install can
prepare manually. Download success means a verified package is ready (network
transfer or valid cache); the pre-install cache recheck is part of installation
preparation, not another download observation.

A true platform Install return emits `launched`, never confirmed success.
Before handing off, one local `pending-update-metrics.json` marker stores only
schema, time, source/target and channel (max 1 KiB). A later target-version startup
emits confirmed once and deletes it. An old source startup retains it without
claiming failure; an unrelated known version emits differentversion and removes
it. Unknown builds do not confirm. Without a usable marker, a known LastRunVersion
to newer running-version change emits one confirmed startup with untracked mode,
covering manual/package-manager updates without claiming an installer method.
Fresh installs and unchanged/downgraded versions do not produce that fallback. Malformed/oversized/reparse-point state is
rejected and markers expire after seven days. Withdrawal deletes pending state;
no user/installation ID is introduced. Confirmation means the target app started,
not that every installer action was verified or that the app never later failed.

Counters/durations require consent at operation start and the same generation
at completion. No asset URL, filename/path, account or server data is attached.
Pending-state IO is bounded and best effort; interruption or persistence failure
can lose confirmation. Existing shared queue/rate/sampling limits still apply.

Memory reporting reads only `Process.GetCurrentProcess().WorkingSet64` after
Refresh and `GC.GetTotalMemory(false)`. The former is the process's resident
working set, including shared/private pages; the latter estimates managed heap
bytes excluding fragmentation and does not force garbage collection. Neither
is a memory dump, private/native allocation total, or a complete ownership
accounting. Do not subtract the heap from working set to infer native memory.

A worker timer reads at most once every five minutes, starting five minutes
after metrics consent is enabled. Monotonic throttling prevents timer catch-up
bursts; overlapping reads are suppressed and failures skip until the next
interval. No process enumeration, system RAM total, machine metadata, inventory
counts or UI-thread sampling is used. `dashynms.memory.working_set` and
`dashynms.memory.managed_heap` are byte gauges. These already rate-limited gauges
retain every sample within queue limits rather than applying duration sampling.
Mode is interactive (a visible non-minimised window) or background/tray according
to the existing activity signal. Withdrawal stops the timer and generation-aware
recording rejects reads crossing withdrawal/reconsent; shutdown disposes it.

Compare sustained values within release, OS, architecture and mode. Windows
working-set trimming and Unix resident-memory accounting/reclamation are not
identical, and GC scheduling means uncollected allocations affect heap estimates.
Cross-platform absolute comparisons and sampled short spikes can mislead. Short
sessions under five minutes produce no memory samples. The real current-process
read test logs elapsed time without a fragile CI performance deadline; manual-
clock tests verify rate/overlap/failure bounds and both modes on all CI platforms.
See the .NET [working set](https://learn.microsoft.com/dotnet/api/system.diagnostics.process.workingset64)
and [managed heap](https://learn.microsoft.com/dotnet/api/system.gc.gettotalmemory)
API definitions for interpretation.

### Session health

`SessionHealth` defines a session as an eligible process interval, including
foreground and tray/background operation. Restart creates a fresh random GUID;
consent enabled midway starts a new interval without backfilling. There is no
persistent installation/user ID and no unique-user or retention claim.
Only version-2 explicit error **and** usage consent is eligible for all outcomes.
Version-1 metrics continue, but cannot enter this denominator. Withdrawal of
either choice purges session state and queued session reports independently of
other allowed usage metrics.

A normal exit is `healthy`, or `errored` if a command/UI error was observed.
`Application`-origin unhandled managed exceptions produce `managedcrash`, even
when the same exception was already captured at the UI hook. A missing terminal
marker produces `abnormal` on the next eligible launch: native crashes, forced
kill, power loss and other unknown fates are not confirmed crashes. UI errors
alone do not prove termination. Unknown outcomes have no invented duration;
their timestamp is recovery time, not an inferred termination time. Fatal reports
are saved synchronously and remain separate from the original error event.

The local marker is strict JSON, at most 2 KiB, atomically replaced with a disk
flush. It has a fresh GUID, consent/schema versions, safe release/platform
context, timestamps, error/sampling flags and optional terminal outcome/duration.
Recovery expires after seven days and rejects malformed, future, oversized or
reparse-point state. A known terminal marker survives a failed queue write and
is retried by ID; withdrawal deletes it. Terminal records use the existing
bounded queue, cancellation, metrics pacing and server backoff. Queue loss,
expiry and retries make counts approximate; running intervals and unknown fates
without a subsequent launch are not visible.

Sentry Release Health support was verified on the EU project with controlled
native `session` fixtures: healthy, errored, crashed and abnormal each indexed.
However, native `avg(session.duration)` returned null, consistent with [Sentry's
session-statistics API documentation](https://docs.sentry.io/api/releases/retrieve-release-health-session-statistics/),
which says durations have not been recorded since January 2023. The [session
protocol](https://develop.sentry.dev/sdk/sessions/) distinguishes unknown fate
from confirmed crashes. We use Application Metrics for the explicit four-outcome
view and durations; this does not populate the native Release Health widget or
consume an additional error event for each session. It uses the existing
Application Metrics allowance, so inspect that project's usage/quota before
increasing volume.

`dashynms.session.outcome` is a counter of 1, with fixed `mode=process`,
`area=application`, release/platform and `outcome=healthy|errored|managedcrash|abnormal`.
`dashynms.session.duration` is a distribution in milliseconds, sampled once at
interval start with probability 1/10 and emitted only for known-ended intervals,
with a 90-day cap. Both points use the fresh interval GUID as their trace ID;
there is no user dimension. For comparable release/platform populations, calculate
managed crash-free known-ended intervals as
`100 * (healthy + errored) / (healthy + errored + managedcrash)` when the denominator
is positive. Display abnormal counts separately; never count them as successful
sessions or describe this as overall/native crash-free reliability. Duration
percentiles cover sampled known endings, including tray time, not active usage.

`tools/SessionHealthProbe` starts actual child processes for clean exit, unhandled
managed failure and forced kill, then restarts against the saved profile. CI runs
it on Windows, macOS and Linux and verifies classifications, recovery delivery
and sanitisation. Unit tests cover consent populations, error refinement, state
bounds and withdrawal. For live verification use a dedicated synthetic release;
confirm all four outcome counters, only three known duration points and no
session-generated Errors. Do not treat envelope HTTP success alone as indexing.

GitHub builds read the public ingestion address from the repository variable
`SENTRY_DSN`. For local builds, set `DASHYNMS_SENTRY_DSN` before publishing. The
address becomes assembly metadata in the app. No authentication token is needed
to build or send reports. A build without a valid HTTPS address disables sharing
and its prompt.

Run the local fatal-error and restart check with:

```powershell
dotnet run --project tools/CrashReportingProbe -c Release
```

The probe crashes an isolated child process, verifies its sanitised report,
then tests next-launch delivery through an in-memory transport. It never
contacts Sentry. CI runs this check on Windows, macOS and Linux alongside
payload, queue, cancellation and consent tests.

### Update download preference

Settings, About has a **Download updates automatically** preference. It defaults
to on. Applying an off value keeps release checks and notifications enabled but
stops automatic package downloads, including a download already in progress.
Verified packages already cached remain available for an explicit update.

Windows, macOS and Linux AppImage installations offer an explicit download and
update action even with automatic downloads off. Each package is checked against
GitHub's size and SHA-256 before the platform installer receives it. Downloads
never start an installation without the user's update action. Linux packages
managed by a package manager keep their notification-only behaviour.

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

The version is pinned in `src/DesktopNMS.Presentation/DesktopNMS.Presentation.csproj`
(`DashyNMSCoreVersion`); the app gets Core through that project. Between Core releases it's a development build of
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

The tools are published under `artifacts/mcp/<runtime>`. `-Runtime` accepts
`win-x64`, `osx-x64`, `osx-arm64`, `linux-x64` and `linux-arm64`, defaulting to
the current host. Publish the same runtime as the app; the app build copies
only that runtime's executable, licence and revision into `mcp/`. Each output
folder must be fresh. The **Native network tools** CI workflow runs a real
stdio handshake, tool discovery and a demo-device query on every target.

Then publish the desktop app:

```bash
dotnet publish src/DesktopNMS.Avalonia/DesktopNMS.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

This publishes a self-contained, single-file Windows x64 app, so
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

### Native packages

The **Native packages** Actions workflow builds Windows x64 Setup, macOS
Intel/Apple Silicon `.dmg`, and Linux x64/ARM64 AppImage artifacts. Run it
manually with a version, or leave the version blank for a CI preview. Changes
to the package builder also run it on pull requests. It uploads artifacts for
30 days, with SHA-256 files and source revisions, without creating a release
or tag. The manual **Preview build** workflow calls this same package workflow,
including the Windows upgrade checks, then publishes all five native packages
as a GitHub prerelease. Stable releases use the same five-platform packaging flow.

Before running **Preview build** on `master`, write and review
`docs/releases/<version>.md` for the next numbered preview (for example
`2.0.0-preview.5.md`). The workflow derives that number from existing preview
tags and fails if the notes are missing or empty. It publishes those exact notes
and creates the tag only after all package and upgrade checks pass. It also
checks all five packages' checksums, versions and source revisions before
publishing. GitHub releases attach only the app downloads; checksum sidecars
and build records remain in the Actions artifacts for verification. Merging a PR does not publish a preview; dispatch the workflow
manually when the notes and build are ready.

On the matching native host, with that runtime's MCP bundle built first:

```powershell
./tools/Publish-Package.ps1 -Runtime osx-arm64 -Version 2.0.0-preview.1
```

Windows also needs `-InnoCompiler <path-to-ISCC.exe>`. Output goes under
`artifacts/packages/<version>/<runtime>`, with fresh staging directories for
each version. The macOS bundle includes the notification bridge, login helper,
icon and native network tools. Development Mac packages use ad-hoc signatures;
preview and stable distribution packages use Developer ID signing and Apple
notarisation. Windows signing is tracked in #109. Linux packages use
checksum-pinned AppImage build tools and runtime.

### macOS distribution signing

Only the **Preview build** and **Release build** workflows dispatched on
`master` can request the `DashyNMS_MacOS` environment. Its required reviewer
approves the Intel and Apple Silicon signing jobs. Ordinary pull-request, push
and **Native packages** runs use ad-hoc signing without that environment.

The environment holds six secrets: `MACOS_CERTIFICATE_BASE64`,
`MACOS_CERTIFICATE_PASSWORD`, `MACOS_SIGNING_IDENTITY`, `APPLE_API_KEY`,
`APPLE_API_KEY_ID` and `APPLE_API_ISSUER_ID`. The signing certificate must include
its private key. The App Store Connect credentials use a team API key.

The temporary keychain includes the public Apple Developer ID G1 and G2
intermediates bundled under `tools/macos/certificates`. The packager requires
exactly one valid identity matching `MACOS_SIGNING_IDENTITY`, then signs with
that certificate's fingerprint. It reports missing or untrusted identities
before signing. Code-signing failures include bounded native diagnostics with
configured credential values redacted.
The packager appends the temporary keychain to the existing user search list
so that `codesign` can find its intermediate issuers. Cleanup restores the
original search list before deleting the temporary keychain.

The distribution packager signs nested native code before sealing the app,
submits the app and disk image to Apple, staples the accepted tickets, and checks
Gatekeeper acceptance before producing release checksums. The temporary signing
keychain and credential files are removed on success or failure. A failed
signing or notarisation step prevents publication.

For a stable release, create its numeric version tag on the current `master`
commit and publish the corresponding GitHub release. The release event dispatches
the build on `master`; the tagged commit must match that run's source commit and
the version in `Directory.Build.props`. To retry, dispatch **Release build** on
`master` with the existing stable tag. It attaches the five verified installers
to that release, without checksum sidecars or build manifests.

To check an installed download on a Mac, run:

```bash
codesign --verify --deep --strict /Applications/DashyNMS.app
spctl --assess --type execute --verbose=4 /Applications/DashyNMS.app
```

The signature check exits successfully without output. Gatekeeper should report
`accepted` and `source=Notarized Developer ID`. To see the signing identity, run
`codesign --display --verbose=4 /Applications/DashyNMS.app`.
These checks use macOS tools and do not require an in-app verification page.

The protected Intel signing job blocks outbound UDP port 443 on its disposable
runner to make Apple's ticket requests use HTTPS over TCP. This follows
[GitHub's workaround for Intel runner connection failures](https://github.com/actions/runner-images/issues/14924).
Apple Silicon and routine package builds do not change their network policy.
Signature, ticket and Gatekeeper checks still have to pass before publication.

CI verifies the finished installer, mounted DMG or extracted AppImage, then
runs the real packaged MCP server against the example network. Separate fresh
Windows runners install the shipped 1.0 and 1.1 apps and upgrade them without a
new install path, checking settings, layouts, window placement, encrypted
secrets, startup registration and the existing shortcut. Installer tests are
restricted to disposable GitHub Actions accounts.


## Layout

```text
src/
  DesktopNMS.Presentation/  View models and services without a UI dependency;
                             references the DashyNMS.Core package
    ViewModels/             Every view model
    Services/               Monitoring, updates and platform service interfaces
    Infrastructure/         Commands, filtering, CSV, UI dispatch interfaces
    Demo/                   Example network used by demo mode and screenshots
    Security/               Encrypted credentials and chat storage on each OS
    WhatsNew.md             Bundled release notes
  DesktopNMS.Avalonia/      Desktop app for Windows, macOS and Linux
    Assets/                 App icon, brand fonts and their licences
    Views/                  Views, dialogs and device detail sections
    Themes/                 Dark/light palette and styles
    Infrastructure/         UI dispatch, dialogs, theme and window placement
    Services/               Windows, tray, notifications and startup adapters
tools/
  Publish-Dev.ps1           Versioned Windows development builds
  Publish-Mcp.ps1           Bundled Intelligence network tools
  Render-Screenshots.ps1   Demo screenshot tour
installer/
  DashyNMS.iss              Windows installer
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

Development builds keep their own data in `avalonia\` inside that folder
(`~/.config/DashyNMS/avalonia` on Linux, `~/Library/Application
Support/DashyNMS/avalonia` on macOS). Windows release and preview builds reuse
the existing 1.x profile; macOS and Linux retain their Avalonia profile.

Single chat mode under **Settings > Intelligence** keeps one conversation in memory. Its messages,
draft, tool history, Spotlight and companion state are never written to the chat archive.
Restarting the app starts a fresh temporary chat, including after a crash. Applying a mode
change stops any running answer before replacing the conversation. Turning the mode on
keeps existing saved chats. Turning it off discards the temporary conversation and restores
the saved chats for the current account and server. ChatGPT sign-in and other settings are kept.
**Reset temporary conversation** or **Ctrl+N** discards the current temporary chat.

Secrets (the `.dat` files and `chats\`) are encrypted for the current user on
every OS (#154):

- **Windows:** DPAPI, with each secret's own purpose string as its entropy.
- **macOS and Linux:** AES-256-GCM with one random key per user. The key lives
  in the login Keychain (through `/usr/bin/security`) or the freedesktop Secret
  Service, such as GNOME Keyring or KWallet (through `secret-tool`, from
  `libsecret-tools`). It's created only when the keychain answers that there
  isn't one. If the keychain can't be reached, nothing is saved and existing
  files are kept for later.

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
