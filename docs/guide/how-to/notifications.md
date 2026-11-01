# Choose which alerts notify you

Notification settings control what this computer tells you. Alerts you leave out still appear in the app and still count. To change when LibreNMS raises an alert for everyone, [edit the alert rule](alert-rules.md) instead.

## Set up your notification selection

1. Open **Settings → Notifications**.
2. Turn on **Show notifications**.
3. Under **Which alerts**, choose the mode that matches what you want. Read its description to check whether the list includes rules or excludes them.
4. If that mode uses a list, choose a rule and select **Add rule**. A rule added here applies to every device.
5. Choose whether critical and warning alerts should notify you, and set their persistence options.
6. Choose **Apply & Save**.

For a choice limited to one device, right-click that device's alert on the Alerts page and use its notification options. Review the resulting list in Settings. If you choose to notify only for listed rules and leave the list empty, nothing will notify you.

## Set quiet hours

1. In **Settings → Notifications**, enable **Suppress notifications overnight**.
2. Set the starting and ending hours.
3. Decide whether to enable **Still notify for critical alerts during quiet hours**.
4. Apply and save your changes.

Quiet hours suppress desktop notifications. They do not stop monitoring or remove alerts from LibreNMS.

## Avoid a burst when you start work

Enable **Stay quiet about alerts that were already open at start-up** to avoid notifications for the existing backlog when the app first polls. Review the Alerts page at the start of your shift so you still see that backlog.

## Keep monitoring while the window is closed

On Windows and macOS, check **Settings → Window and start-up** for the close-to-tray and start-up choices. DashyNMS must remain running to notify you. On Linux, closing the window quits the app.

If popups still do not appear, follow the [notification troubleshooting steps](../reference/troubleshooting.md#notifications-do-not-appear). Operating-system notification permissions and Do Not Disturb settings can also suppress them.
