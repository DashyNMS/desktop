# 2.0.0
Preview, 20 October 2026

## New
- **Intelligence.** Ask about your network in plain words - what's alerting, how a switch is doing, what changed overnight - and get an answer from your own ChatGPT plan, with the evidence it used. It only reads; it can't change anything. {search}
- **Answers that show their working.** See what was looked up and when, with devices, states and alert severities marked in the answer, and LibreNMS's graphs shown inline. {chart}
- **Saved chats.** Keep several conversations, pin the ones you use, and pick up where you left off. An unfinished answer can be retried or the question edited. {star}
- **Spotlight and context.** Keep a conversation to chosen devices, a device group or a location, with the devices and alerts it mentions beside it. {device}
- **Web search, with sources.** Intelligence can check public information, such as firmware releases, and cites what it found. You can turn it off in Settings. {link}

## Improved
- **Log entries open where they belong.** Clicking an entry on the dashboard or in Logs opens that device's Graylog, event log or alerts with the entry selected. {logs}
- **More Graylog messages named.** A message's source now matches its device by sysName or short host name, not just the full host name. {search}
- **Defaults in one place.** Default map now sits with the other start-up choices in Settings, Window and start-up. {settings}

## Fixed
- **Neighbours.** Neighbours now load for ping-only devices, showing the switch that sees them. {link}
- **Logs filters.** The device and type filters show "All devices" and "Any type" instead of blank boxes. {logs}