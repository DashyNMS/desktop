# Search and export logs

Use logs to compare what happened before and after a fault. Start with a device if you know where the problem is, or with Logs for a network-wide search.

![Network event log with filters](../../screenshots/logs.png)

## Find events around an incident

1. Open **Logs** and choose the Event log or Alert log from its navigation menu.
2. Set the available filters and search for the device or event text.
3. Load more entries if the period you need is not visible. The network log views support up to 1,000 entries.
4. Open an entry's device to compare its status and graphs.

For one device, open its **Event log** or **Alert history**. These keep the investigation focused on that device.

Search filters the entries the view has loaded. If you cannot find an older event, widen the available filters or load more history before concluding that it is absent.

## Search Graylog messages

1. [Connect Graylog](integrations.md#connect-graylog) if you have not set it up.
2. Choose Graylog under **Logs**, or open a device's Graylog view.
3. Adjust the time range, severity and search controls for your investigation.
4. Check the device match if messages appear in the network view but not under the device.

Graylog uses its own server, credentials and stored messages. A working LibreNMS connection does not establish a Graylog connection.

## Copy or save the entries

In Graylog, or a device's Event log or Alert history, use the CSV export control and choose to copy or save the results.

- Graylog exports the current page of messages.
- Device Event log and Alert history export loaded entries.
- The current search and row sort order apply to these exports.

An export is not a full archive of the server's logs. Check that the entries and period you need are loaded before sharing the file.

## Put useful logs on your dashboard

Add an Event log or Graylog widget and [set its Spotlight](dashboard.md#focus-a-log-widget-on-a-site) to a device, group or location. This is useful for a site you are watching during maintenance.
