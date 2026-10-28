# Privacy

DashyNMS does not require a DashyNMS account. Error reports and basic usage sharing are optional and off until you explicitly allow them.

This document describes the current application. The [website privacy page](https://dashynms.pckp.net/desktop/privacy/) may describe an earlier release.

## Who it talks to

- **Your LibreNMS server**, the one you sign in to. This is where everything you see comes from.
- **Your Graylog and Unimus servers**, only if you set them up in Settings → Integrations.
- **GitHub**, to check for and download new versions of DashyNMS from this repository's [Releases](https://github.com/DashyNMS/desktop/releases). It sends nothing about you or your network, only the ordinary request any download makes.
- **Sentry**, only for the error reports or basic usage statistics you choose to share, as described below.
- **OpenStreetMap's tile servers**, for the map backgrounds on the Geographical map, unless your LibreNMS server or Settings names a different tile server. Like any map website, the tile server sees which areas of the map are loaded.

Error and usage reports exclude monitored network data.

## Optional error and usage sharing

The first normal launch after this feature becomes available asks for your choice. Both boxes start unchecked. You can allow either category, both categories, or choose **Don't share**. Closing the window leaves sharing off and asks again on the next launch. An explicit choice is remembered. Change it in **Settings → About**, then apply the change.

- Error reports contain the app version, operating system, processor architecture, time, exception types, and method names from approved application assemblies. They exclude exception messages, file paths, log files, network addresses, credentials and Intelligence content.
- Basic usage sharing sends one `dashynms.app_start` counter measurement per application process after you enable it. It contains the version, operating system, architecture and time. There is no user or installation identifier, so these measurements count launches rather than unique users.

Launch counts use Sentry Application Metrics, separately from error events. Metrics ingestion requests disable IP and user-agent inference.

Sentry receives the connection information necessary to accept a report, including its source IP address. Reports are therefore not described as anonymous. The configured project uses Sentry's EU ingestion endpoint. See [Sentry's privacy policy](https://sentry.io/privacy/) for its handling of service data.

Consented reports can wait locally in the application's `crash-reports` directory while offline. The queue holds at most ten reports of at most 32 KiB each and expires them after seven days. Disabling a category cancels its pending delivery and deletes its queued reports. This does not remove reports already received by Sentry. Server retention follows the Sentry project's configured retention policy.

Fatal managed errors are saved for delivery on a later launch. Native crashes and memory dumps are not sent. Demo and screenshot modes do not report. Local diagnostic logs are separate and are never attached to these reports.

## What's kept on your computer

Everything DashyNMS saves stays on your computer, under your Windows account:

- **`%APPDATA%\DashyNMS`:** your server address and settings, dashboards and map layouts, downloaded updates, and log files kept for 14 days. Your LibreNMS API token, Graylog password and Unimus token are encrypted with Windows (DPAPI), so only your Windows account on this computer can read them. You can turn off saving the LibreNMS token entirely.
- **`%LOCALAPPDATA%\DashyNMS`:** cached map tiles.

**Signing out removes all of it** apart from the logs and downloaded updates, and DashyNMS restarts ready to sign in again. Uninstalling asks whether to remove these folders too. Keep them and a reinstall picks up where you left off; a silent uninstall always keeps them.

## Questions

Open an [issue](https://github.com/DashyNMS/desktop/issues), or for anything sensitive, see [SECURITY.md](SECURITY.md).
