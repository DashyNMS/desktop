# Schedule maintenance and rediscover devices

These actions change your LibreNMS server. Your account needs permission for the action, and other users of the server can see the result.

## Schedule a maintenance window

1. Open **Devices** and select the device you will work on.
2. Right-click and choose **Maintenance…**.
3. Choose **Now**, or **At a scheduled time** for work later.
4. Check the start date, time and duration in the dialog. Scheduled times use a 24-hour clock, such as `17:30`.
5. Add a title or notes for your team, and review **Alert behaviour**.
6. Choose **Schedule** and check the result before starting work.

For several devices, select them together first. The menu shows how many devices the maintenance action covers. Check that count and your selection before submitting.

Maintenance changes how LibreNMS handles the selected devices during the window. Confirm the window covers the expected work, including recovery checks. Your local [quiet hours](notifications.md#set-quiet-hours) are a separate setting and do not put devices into maintenance.

You cannot cancel a scheduled window through DashyNMS. To end one early, use LibreNMS's web interface.

## Ask LibreNMS to rediscover a device

Use rediscovery after changes that affect the device information LibreNMS discovers, such as added interfaces.

1. Select the device in **Devices**.
2. Right-click and choose **Rediscover**.
3. Check the action result, then allow LibreNMS time to process the request.
4. Refresh or revisit the device to check the discovered information.

With several devices selected, the action is **Rediscover all**. A successful request means LibreNMS accepted it; it does not mean that discovery has already finished.

If information stays out of date, check discovery and polling on the LibreNMS server. Increasing the app's refresh frequency does not make LibreNMS collect new samples sooner.

## Check the network after the work

Open the affected devices, compare their status and ports, and review [alerts](alerts.md) and [logs](logs.md). An acknowledgement is a record of who is handling an alert; it is not evidence that maintenance succeeded.
