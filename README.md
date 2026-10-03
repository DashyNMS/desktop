<p align="center">
  <img src="https://dashynms.pckp.net/assets/img/favicon.svg" alt="" width="96" height="96">
</p>

<h1 align="center">DashyNMS</h1>

<p align="center">
  <strong>Your LibreNMS network, right on your desktop.</strong><br>
  For Windows 10 and 11.
</p>

<p align="center">
  <a href="https://dashynms.pckp.net/">Website</a> ·
  <a href="https://github.com/DashyNMS/desktop/releases/latest">Download</a> ·
  <a href="PRIVACY.md">Privacy</a>
</p>

---

Keep an eye on your network without living in a browser tab. Sign in once and DashyNMS watches your LibreNMS server from the notification area, tells you the moment something changes, and gives you a full desktop app for devices, alerts, rules, maps and graphs when you need to dig in.

It works with any [LibreNMS](https://www.librenms.org/) server, and has a companion app for your phone, [DashyNMS Mobile](https://github.com/DashyNMS/mobile).

## What you can do

**Know when something's wrong**
A Windows notification when an alert fires or clears, with Acknowledge right on it. Choose how insistent each severity is, and set quiet hours. The tray icon turns amber or red with the number of open alerts, and a click on it shows the worst of them without opening the app.

**See what matters first**
Build your own dashboard from widgets: open alerts, device status, sensor readings, graphs, the event log, Graylog messages, pinned and recently viewed devices. Drag and resize them however you like, or start from a ready-made one.

**Get to the bottom of it**
Open any alert to see exactly why it fired and what its rule is looking for. Acknowledge it with a note so your team knows you're on it, and filter, search or export the list.

**Every device, in detail**
A sortable, filterable list of your whole network. Open a device to see its status, hardware, ports, sensors, neighbours, VLANs, MAC and ARP tables, graphs and event log. Pin the ones you check most so they're always at the top.

**Take action**
Add devices one at a time or in bulk, put them into maintenance, ask LibreNMS to check them again, or connect straight to them over SSH, Telnet or the web.

**Manage your alerting**
A full alert rule editor, as on the LibreNMS website: build conditions, import from another rule or an SQL query, and choose which devices, groups and locations a rule covers. Edit alert templates and see which rules use each one.

**See how it all connects**
Network and geographical maps, maps of your own, and a view of each device's neighbours. Health brings temperature, fan, optical power and signal readings from across your network into one place.

**More when you need it**
Connect Graylog to search your logs alongside your devices, and Unimus to see each device's configuration backups. If your server has a backup address, DashyNMS switches to it when the main one stops answering.

**Made for Windows**
Dark or light, with your choice of accent colour. It updates itself when a new version is ready.

## Private by design

DashyNMS talks to your own LibreNMS server, and to Graylog and Unimus only if you set them up. There's no account to create, no analytics and no tracking. Your API token is encrypted for your Windows account and never leaves your computer except to go to your server. Signing out removes everything DashyNMS has saved. Read the full [privacy notes](PRIVACY.md).

## Get the app

Download the installer from the [latest release](https://github.com/DashyNMS/desktop/releases/latest) and run it. It installs just for your Windows account, so you don't need admin rights.

## What you need

- Windows 10 (version 1809 or later) or Windows 11.
- A LibreNMS server you can reach from your computer, with its API turned on.
- An API token from it. In LibreNMS, open your user menu, then **API → API Settings → Create API access token**. If you don't have access, your LibreNMS administrator can make one for you in a couple of minutes.

Open DashyNMS, enter your server's address and paste the token. That's it. Closing the window keeps DashyNMS running in the notification area, so it can still tell you when something needs attention.

## Help and feedback

- Found a bug or have an idea? [Open an issue](https://github.com/DashyNMS/desktop/issues). Planned work is tracked there too, under the [1.1.0](https://github.com/DashyNMS/desktop/milestone/2) and [1.2.0](https://github.com/DashyNMS/desktop/milestone/3) milestones.

---

<sub>DashyNMS isn't affiliated with LibreNMS. LibreNMS is a trademark of its respective owners.<br>
The source is published for reference and security review only. All rights reserved; see [LICENSE](LICENSE).<br>
Found a security problem? See [SECURITY.md](SECURITY.md). Technical notes are in the [developer guide](docs/DEVELOPMENT.md).</sub>
