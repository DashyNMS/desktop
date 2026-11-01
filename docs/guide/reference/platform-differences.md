# Differences between Windows, macOS and Linux

A few things work differently on Windows, macOS and Linux. This page lists them.

## The tray icon

| | Windows | macOS | Linux |
| --- | --- | --- | --- |
| Where the icon is | The notification area, by the clock | The menu bar, at the top right | The system tray, if your desktop has one |
| Closing the window | Keeps DashyNMS running there | Keeps DashyNMS running there | Quits DashyNMS |
| Start hidden | Available | Available | Not available |

You can change the first two in **Settings → Window and start-up**.

On Linux, closing the window always quits. Many Linux desktops have no tray, and GNOME doesn't show one unless you add an extension. If DashyNMS hid itself there, you might have no way to get it back. If your desktop does have a tray, the DashyNMS icon still appears in it while the window is open.

To see the tray on GNOME, install the **AppIndicator and KStatusNotifierItem Support** extension. Ubuntu includes it already.

## Notifications

On Windows and macOS, an alert notification has an **Acknowledge** button, and clicking the notification opens DashyNMS.

On Linux, the buttons depend on your desktop. GNOME, KDE Plasma, Cinnamon and Xfce show them. Some lighter desktops and notification programs show the message without buttons, or ignore clicks. The alert is still in DashyNMS's Alerts list, where you can acknowledge it.

## Starting when you sign in

Turn on **Start DashyNMS when I sign in** in **Settings → Window and start-up**.

- **Windows** adds DashyNMS to the apps that start when you sign in. You can see it in **Task Manager → Startup apps**.
- **macOS** adds it to your login items. You can see it in **System Settings → General → Login Items**.
- **Linux** adds a file to `~/.config/autostart`, which most desktops follow. If you move the AppImage, turn the setting off and on again so it points at the new place.

If you run DashyNMS inside a Flatpak sandbox, it can't start itself when you sign in. Use your desktop's own startup settings instead.

## Updates

| | How updates install |
| --- | --- |
| Windows | **Restart to update** runs the new installer and reopens DashyNMS. |
| macOS | **Relaunch** swaps in the new app and reopens it, when DashyNMS is in Applications. |
| Linux | **Restart to update** replaces the AppImage file and reopens it. |

DashyNMS checks each update against the release on GitHub before using it. If you installed DashyNMS another way on Linux, for example through a package manager, DashyNMS tells you about new versions but leaves installing them to your package manager.

## Saved passwords

| | What protects them |
| --- | --- |
| Windows | Windows encryption for your account |
| macOS | A key in your login Keychain |
| Linux | A key in your keyring, such as GNOME Keyring or KWallet |

On Linux, if your desktop has no keyring, or `secret-tool` isn't installed, DashyNMS can't save your API token. It works, but asks for the token each time it starts.

## Signing in with LibreNMS

**Sign in with LibreNMS** opens your server's sign-in page in a small browser window inside DashyNMS: Microsoft Edge WebView2 on Windows, Safari's engine on macOS and WebKitGTK on Linux. It trusts the same certificates as your system. If your server uses a private certificate your system doesn't trust, sign in with an API token instead.
