# Install on Linux

DashyNMS for Linux comes as an AppImage, a single file that runs on most distributions without installing anything. There's one for 64-bit Intel and AMD computers (x64) and one for 64-bit ARM computers (ARM64). You need a desktop session, such as GNOME, KDE Plasma, Cinnamon or Xfce.

There's no Flatpak, Snap, `.deb` or `.rpm` package yet.

## Before you start

DashyNMS uses a few things most desktop distributions already have. If something doesn't work, check these are installed:

| What for | Package on Debian and Ubuntu |
| --- | --- |
| Signing in with LibreNMS | `libgtk-3-0`, `libwebkit2gtk-4.1-0` and `libsoup-3.0-0` |
| Saving your API token and passwords | `libsecret-tools`, and a keyring such as GNOME Keyring or KWallet |
| Desktop notifications | `libnotify-bin` |

Other distributions use similar names. Look for GTK 3, WebKitGTK 4.1, libsoup 3, libsecret and libnotify.

Without the sign-in packages, you can still sign in by pasting an API token. Without a keyring, DashyNMS can't save your token, so it asks for it each time it starts.

## Install

1. Go to the [releases page](https://github.com/DashyNMS/desktop/releases) and download the AppImage for your computer:
   - Intel or AMD: `DashyNMS-<version>-linux-x64.AppImage`
   - ARM: `DashyNMS-<version>-linux-arm64.AppImage`
2. Move it somewhere it can stay, for example a folder called `Applications` in your home folder.
3. Allow it to run as a program. In most file managers, open the file's **Properties**, and turn on **Allow executing file as program** (or **Is executable**). Or, in a terminal:

   ```bash
   chmod +x DashyNMS-*-linux-x64.AppImage
   ```

4. Double-click it to open DashyNMS.

If it doesn't open, your system may be missing FUSE, which AppImages use. The [AppImage documentation](https://docs.appimage.org/user-guide/troubleshooting/fuse.html) explains how to add it for your distribution.

## Update

DashyNMS checks for new versions by itself. When one is ready, choose **Restart to update**. DashyNMS downloads the new AppImage, checks it, puts it in place of the old file and opens again.

For this to work, the folder holding the AppImage has to be one you can write to, and you need to open the AppImage file itself rather than a shortcut (symbolic link) to it.

You can also download the new AppImage yourself, make it executable, and delete the old one.

## Uninstall

1. Quit DashyNMS by closing its window.
2. Delete the AppImage file.

If you turned on **Start DashyNMS when I sign in**, turn it off before you delete the file, or remove `~/.config/autostart/net.dashynms.desktop.desktop` afterwards.

Your settings and saved sign-in stay behind, in case you install it again. [Where DashyNMS keeps your data](your-data.md#linux) explains how to remove them too.
