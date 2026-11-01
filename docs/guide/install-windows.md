# Install on Windows

DashyNMS runs on Windows 10 (version 1809 or later) and Windows 11, on 64-bit Intel and AMD processors. It installs just for your own Windows account, so you don't need to be an administrator.

## Install

1. Go to the [releases page](https://github.com/DashyNMS/desktop/releases) and download `DashyNMS-Setup-<version>.exe`.
2. Open the file you downloaded.
3. Windows may show **Windows protected your PC**, because the installer isn't signed yet. Choose **More info**, then **Run anyway**.
4. Follow the steps in the installer.

DashyNMS goes into `%LOCALAPPDATA%\Programs\DashyNMS` and adds itself to the Start menu. Open it from there.

To sign in with LibreNMS, DashyNMS uses Microsoft Edge WebView2, which Windows 11 already has. If Windows 10 asks for it, install the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) from Microsoft. You can still sign in with an API token without it.

## Update

DashyNMS checks for new versions by itself. When one is ready, choose **Restart to update**. Your settings and sign-in stay as they are.

You can also download a newer installer and run it over the top of the version you have.

If you used DashyNMS 1.x, 2.0 picks up your existing settings, dashboards and saved sign-in.

## Uninstall

1. Exit DashyNMS. Right-click its icon in the notification area, by the clock, and choose **Exit**.
2. Open **Settings → Apps → Installed apps**, find **DashyNMS**, and choose **Uninstall**.
3. The uninstaller asks whether to remove your settings, dashboards, maps and saved sign-in as well. Keep them if you might install DashyNMS again, and it'll pick up where you left off.

[Where DashyNMS keeps your data](your-data.md#windows) lists the folders, if you'd rather remove them yourself.
