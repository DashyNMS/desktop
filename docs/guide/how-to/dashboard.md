# Build your dashboard

Make the dashboard the first place you check during a shift. Start with open alerts and device status, then add the graphs and sensors you use most.

![Dashboard with monitoring widgets](../../screenshots/dashboard.png)

## Add and arrange widgets

1. Open **Dashboard** and choose **Edit dashboard**, the pencil button.
2. Choose **Add widgets** and select the information you want to show.
3. Configure the widget where it asks for a device, graph or time range.
4. Drag widgets to arrange them. Resize them to give a graph more space or keep counts compact.
5. Choose **Done** to finish editing.

Use **Undo** while editing to reverse a change, or **Cancel** to abandon the editing session. **Tidy up** helps arrange the widgets if the layout becomes awkward.

A useful first layout has open alerts near the top, device status beside them and a traffic graph underneath. Leave room for the event log so you can compare an alert with recent changes.

## Keep a sensor in view

1. Open **Health** so DashyNMS has loaded the available sensors.
2. Return to the dashboard and add a sensor widget, or select an existing sensor widget while editing.
3. Choose **Add sensor** and search by device or sensor description.
4. Add the readings you want and finish editing.

If a sensor cannot be found, check whether it appears in Health. DashyNMS can only offer readings that LibreNMS supplies.

## Focus a log widget on a site

1. Add or select an Event log or Graylog widget in dashboard editing mode.
2. Under **Spotlight**, choose **Add**.
3. Select the devices, group or location you want the card to cover.
4. Check the summary of what the card will show, then finish editing.

Choose **Show the whole network** in the widget's setup to remove that focus. Graylog widgets need a working [Graylog connection](integrations.md#connect-graylog).

## Keep favourite devices nearby

Pin devices from the [Devices list](devices.md#find-and-pin-a-device), then add a pinned-devices widget. Recent devices are useful for investigations where you move between several hosts.

Next, [choose which alerts notify you](notifications.md) so your desktop notifications match the work you follow on the dashboard.
