# Troubleshooting

Start with the section closest to what you see. Keep the error message and the time it happened; both help distinguish an app problem from a server or network problem.

## Cannot connect to LibreNMS

1. Open the same server address in your browser on this computer. If it fails there too, check your network, VPN and server availability.
2. Check the full address, including `https://` and any installation path.
3. Confirm the account has API access and the token is still valid. Being able to sign in to the website does not by itself confirm API access.
4. If browser sign-in fails, try the manual API-token route in [Getting started](../getting-started.md#using-an-api-token-instead).
5. For a private certificate, check trust on your computer. The embedded sign-in browser uses system trust; an API certificate exception does not change browser trust.

Do not post your API token, sign-in QR code or passwords in an issue.

## Devices, alerts or graphs are missing

1. Clear search and filters, including severity and acknowledged-alert choices.
2. Check the same device or alert in LibreNMS using the same account.
3. Check that account's access to the device and the API.
4. For a graph, try a wider time range and confirm LibreNMS has collected samples.
5. If both the app and website show old data, check LibreNMS polling and discovery.

For historical recovered alerts, check **Settings → Polling → Also fetch recovered alerts**. This can return a large amount of history.

## Notifications do not appear

1. Check that DashyNMS is still running. Closing the window quits it on Linux.
2. Open **Settings → Notifications** and check **Show notifications**, severity choices and the rule selection.
3. Check quiet hours and whether alerts already open at start-up are suppressed.
4. Check the operating system's notification permission and Do Not Disturb settings.
5. Use the Alerts page to confirm whether there is a new alert transition to notify about.

Notification buttons vary between Linux desktops. If an acknowledgement button is absent, acknowledge the alert [inside the app](../how-to/alerts.md#acknowledge-with-a-note).

## Alert ages are wrong by whole hours

Check **Settings → Polling → The LibreNMS server stores timestamps in UTC**. Leave it off unless your server stores those timestamps in UTC and the displayed ages are wrong by a fixed number of hours. Graylog has a separate display time-zone setting.

## Graylog or Unimus is empty

Run **Test connection** in the integration's Settings page. DashyNMS connects from your computer, so server-to-server connectivity alone is not enough. If the test succeeds, check the device identity and message query or backup matching described in [the integration guide](../how-to/integrations.md).

## The app forgets my token on Linux

Check that `secret-tool` is installed and your desktop's Secret Service keyring is running and unlocked. Without a usable keyring, DashyNMS can connect but cannot save the token for the next launch. See [platform differences](platform-differences.md#saved-passwords).

## Report a problem

1. Note the DashyNMS version, operating system and whether this is a preview build.
2. Write the steps that reproduce the problem, what you expected and what happened.
3. Open **Settings → Diagnostics → Open log folder** and inspect the log for the time of the problem.
4. Remove credentials and private network details from any excerpt or screenshot you share.
5. [Open an issue](https://github.com/DashyNMS/desktop/issues) with those details and the relevant error text.

Keep your settings while investigating. If you decide to reset the app, read [what signing out and deleting data remove](your-data.md#starting-again-from-scratch) first.
