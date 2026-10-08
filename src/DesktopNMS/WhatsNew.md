# 1.1.0
Released 18 October 2026

## New
- **Sign in with LibreNMS.** Sign in on your server's own page and DashyNMS creates its API token for you, so there's no token to find and paste. {lock}
- **Choose which alerts notify you.** Leave out noisy rules, or only hear about the ones that matter, on every device or just one. {bell}
- **Maps.** Network, geographical and custom maps, with an editor to draw your own. {map}
- **Neighbours.** Every neighbour at a glance, filtered down with Neighbourhoods. {link}
- **Logs for your whole network.** The event log, alert log and Graylog for every device, each a click away under Logs. {logs}
- **More for your dashboard.** Top interfaces, errors and devices, the event log, Graylog, and any port's traffic graph. {chart}
- **Unimus config backups.** See a device's config backups, compare them, and save either to a file. {copy}
- **Bulk add devices.** Paste a list or import a CSV to add many devices at once. {add}
- **Maintenance for several devices.** Select them in Devices and put them all into maintenance in one go. {wrench}
- **Backup server address.** DashyNMS switches over when the server stops answering, and you choose when to switch back. {swap}
- **Background updates.** Updates download while you work. Restart when it suits you. {download}

## Improved
- **A new look.** Sidebar navigation, a themed title bar and the fonts from DashyNMS Mobile. {look}
- **Editing your dashboard.** One Edit button, set-up beside the widget with a live preview, sizes to pick from, Undo and Duplicate. {grid}
- **Graphs.** In the app's own colours, with a legend to turn series on and off, and any port's graphs a click away. {chart}
- **Alerts.** See how often an alert has fired before, and get a clear confirmation, with Undo, when you acknowledge. {bell}
- **Tray.** A quick look on left-click, an icon that shows the alert state, and your choice of which severities it counts. {tray}
- **Read-only tokens.** Buttons for changes your API token can't make are turned off, with the reason. {lock}

## Fixed
- **Time zones.** Timestamps with a time zone are no longer shifted twice. {clock}
- **Request timeout.** A new timeout in Settings applies straight away, not after a restart. {clock}
- **Redirects.** The API token is never sent to a host a redirect points at. {shield}
- **Sign out.** Signing out clears everything and goes straight to sign-in. {signout}
- **Uninstall.** Uninstalling removes everything DashyNMS added. {package}
