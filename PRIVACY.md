# Privacy

DashyNMS desktop doesn't collect, store or share any of your data. There's no DashyNMS account, no analytics, no advertising and no tracking.

The same policy is on the website: **<https://dashynms.pckp.net/privacy/>**.

## Who it talks to

- **Your LibreNMS server**, the one you sign in to. This is where everything you see comes from.
- **Your Graylog and Unimus servers**, only if you set them up in Settings → Integrations.
- **GitHub**, to check for and download new versions of DashyNMS from this repository's [Releases](https://github.com/DashyNMS/desktop/releases). It sends nothing about you or your network, only the ordinary request any download makes.
- **OpenStreetMap's tile servers**, for the map backgrounds on the Geographical map, unless your LibreNMS server or Settings names a different tile server. Like any map website, the tile server sees which areas of the map are loaded.

Nothing else is contacted, and nothing about your network is sent to anyone but your own servers.

## What's kept on your computer

Everything DashyNMS saves stays on your computer, under your Windows account:

- **`%APPDATA%\DashyNMS`:** your server address and settings, dashboards and map layouts, downloaded updates, and log files kept for 14 days. Your LibreNMS API token, Graylog password and Unimus token are encrypted with Windows (DPAPI), so only your Windows account on this computer can read them. You can turn off saving the LibreNMS token entirely.
- **`%LOCALAPPDATA%\DashyNMS`:** cached map tiles.

**Signing out removes all of it** apart from the logs and downloaded updates, and DashyNMS restarts ready to sign in again. Uninstalling removes the app but leaves these folders, so a reinstall picks up where you left off; delete them to remove everything.

## Questions

Open an [issue](https://github.com/DashyNMS/desktop/issues), or for anything sensitive, see [SECURITY.md](SECURITY.md).
