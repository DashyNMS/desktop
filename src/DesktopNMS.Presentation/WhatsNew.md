# 2.0.0
Preview 7, 24 October 2026

## New
- **Sensor state definitions.** Supported numeric states gain meaningful names and status colours in Health, Device Details and dashboard sensors. Raw readings remain available in tooltips and graphs. {chart}
- **Manage definitions in Settings.** Enable independently updated definitions, use the official catalogue or a compatible custom repository, and check or refresh the installed catalogue. Intelligence uses the same definitions. {settings}
- **Ask Intelligence.** Right-click a network object, including objects on dashboard widgets, to start a chat with it in Spotlight. Add objects mentioned in replies to Spotlight for follow-up questions. {search}
- **Apply Settings.** Save changes with Apply and keep Settings open. {settings}
- **Windows, macOS and Linux.** The Avalonia app now brings the same views and Intelligence to all three platforms, with native downloads, notifications and sign-in startup. {device}
- **Logs that keep up.** Event log and Alert log now have auto-refresh intervals, keeping your filters and selection. {logs}
- **Intelligence.** Ask about your network in plain words - what's alerting, how a switch is doing, what changed overnight - and get an answer from your own ChatGPT plan, with the evidence it used. It only reads; it can't change anything. {search}
- **Answers that show their working.** See what was looked up and when, with devices, states and alert severities marked in the answer, and LibreNMS's graphs shown inline. {chart}
- **Choose how much it says.** Brief, Balanced, Detailed or a Full report, for each chat. Ask for more or less in a question and it follows that instead. {grid}
- **Saved chats.** Keep several conversations, pin the ones you use, and pick up where you left off. An unfinished answer can be retried or the question edited. {star}
- **Spotlight and context.** Keep a conversation to chosen devices, a device group or a location, with the devices and alerts it mentions beside it. {device}
- **Web search, with sources.** Intelligence can check public information, such as firmware releases, and cites what it found. You can turn it off in Settings. {link}
- **Grouped alerts for LibreNMS 26.10.** A rule firing on several cores, ports or sensors of one device is one alert. Open it with the arrow to see each fault, and acknowledge them together or, where LibreNMS allows, one at a time. {bell}

## Improved
- **Sensor matching and offline use.** Definitions can match device context and OIDs, with cached definitions available after offline restarts. Unsupported states remain unknown, with tooltip explanations and no numeric threshold bar. {chart}
- **Device overview.** Cards fit the available space, with clearer graph, resource and sensor icons. Health uses the same sensor icon as Device Details. {device}
- **Windows upgrades.** Keep your existing 1.x settings and saved sign-in when installing this preview. Updates choose and verify the download for your operating system and processor. {settings}
- **Sensor refresh.** Slow requests have longer to finish, and a failed refresh keeps the last good readings. Missing readings show as unavailable. {chart}
- **Rules for LibreNMS 26.10.** Set Notify per entity and Max entities in the rule editor. Rules that use device columns 26.10 removed are marked for review. {wrench}
- **Accounts with limited access.** Counts say they cover what this account can see, and an alert or graph it can't reach says so instead of failing. {lock}
- **Log entries open where they belong.** Clicking an entry on the dashboard or in Logs opens that device's Graylog, event log or alerts with the entry selected. {logs}
- **More Graylog messages named.** A message's source now matches its device by sysName or short host name, not just the full host name. {search}
- **Defaults in one place.** Default map now sits with the other start-up choices in Settings, Window and start-up. {settings}

## Fixed
- **Intelligence menus and chats.** Object menus open on the first right-click. New chats receive focus and support Ctrl+N, and model and detail choices stay readable while busy. {search}
- **Neighbours and macOS naming.** Unmonitored neighbours remain readable, and the macOS application menu shows DashyNMS. {device}
- **Notification guidance.** Removed the outdated notifications preview warning in Settings. {bell}
- **Windows and the tray.** Close keeps the app running when close-to-tray is enabled. Only the main window appears in the taskbar, and Device Details caption buttons reach the right edge. {device}
- **Branding and alerts.** Windows notifications carry the DashyNMS name and logo, the sidebar uses the worst counted severity, and demo mode shows its alerts. {bell}
- **Intelligence activity.** Outcome icons are centred in their circles. {search}
- **Neighbours.** Neighbours now load for ping-only devices, showing the switch that sees them. {link}
- **Logs filters.** The device and type filters show "All devices" and "Any type" instead of blank boxes. {logs}
