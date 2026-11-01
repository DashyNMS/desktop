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
- On supported systems, choose whether to start at sign-in and whether closing the window leaves DashyNMS running in the tray.

See [platform differences](../reference/platform-differences.md) for the behaviour on your operating system.

## Sign in DashyNMS Mobile

1. Open **Settings → Server**.
2. Choose **Show** under **Sign in DashyNMS Mobile**.
3. Scan the code with your phone's camera and confirm on the phone.
4. Choose **Hide now** when finished. The code also hides after a minute or when you leave the page.

The code contains your API token. Keep it out of screenshots and shared screens. The phone shares this computer's token, so revoking that token signs out both. Enter a separate token on the phone if you need to revoke its access independently.
