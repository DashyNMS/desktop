# Change your connection and desktop settings

Open Settings from the gear button. Choose **Apply** to apply changes while keeping Settings open, or **Apply & Save** when you are finished.

## Change the server address or token

1. Open **Settings → Server**.
2. Enter the server address. Keep any path prefix your LibreNMS installation requires.
3. Paste a replacement API token if needed. Leave the field blank to keep the current token.
4. Apply and save. DashyNMS checks the connection before saving the change.

For a new sign-in, follow [Getting started](../getting-started.md). Signing out removes local settings and saved credentials, so it is not the first step for a temporary connection problem.

## Add a backup address

Enter **Backup address** under **Settings → Server**. It must be another route to the same LibreNMS server, rather than a separate network or installation.

DashyNMS uses the backup after the main address fails twice in a row, and stays on it until you switch back. Check that your computer can reach both routes and that HTTPS certificates are valid for your setup.

## Change refresh timing

Open **Settings → Polling** and adjust **Refresh interval** or **Request timeout**. The refresh interval also applies to Devices and Health after you have opened those tabs.

A shorter interval asks LibreNMS for data more often. It does not make LibreNMS poll equipment faster. If readings are stale on both the website and the app, investigate server-side polling.

## Choose your appearance and opening tab

- In **Settings → Theme**, choose Dark, Light or Match the system and adjust the accent colour.
- In **Settings → Window and start-up**, choose the opening tab, default log and default map.
- In **Settings → Devices**, choose whether a device's Ports view shows a table or cards. See [ports as a table or cards](devices.md#see-ports-as-a-table-or-cards).
- On supported systems, choose whether to start at sign-in and whether closing the window leaves DashyNMS running in the tray.

See [platform differences](../reference/platform-differences.md) for the behaviour on your operating system.

## Choose an update channel

1. Open **Settings → Updates**.
2. Under **Update preferences**, choose an **Update channel**. DashyNMS checks it straight away.
3. Choose **Apply** to keep it.

| Channel | What you're offered |
| --- | --- |
| **Stable** | Finished releases. The default, and best for most people. |
| **Nightly** | A build each night there are changes, as well as releases. Newer features, and sometimes new problems. |
| **Edge** | A build of every change, as well as releases. For testing: it may not work. |

Your settings stay as they are when you switch. Moving from Nightly or Edge to Stable doesn't go back a version: you keep the build you have until a newer release. To go back sooner, install the release over it from the [releases page](https://github.com/DashyNMS/desktop/releases).

## Sign in DashyNMS Mobile

1. Open **Settings → Server**.
2. Choose **Show** under **Sign in DashyNMS Mobile**.
3. Scan the code with your phone's camera and confirm on the phone.
4. Choose **Hide now** when finished. The code also hides after a minute or when you leave the page.

The code contains your API token. Keep it out of screenshots and shared screens. The phone shares this computer's token, so revoking that token signs out both. Enter a separate token on the phone if you need to revoke its access independently.
