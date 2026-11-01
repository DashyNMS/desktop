# Getting started

## What you need

- A computer running one of these:
  - Windows 10 (version 1809 or later) or Windows 11, on a 64-bit Intel or AMD processor.
  - macOS 13 Ventura or later, on an Apple silicon or Intel Mac.
  - Linux with a desktop, on a 64-bit Intel, AMD or ARM processor.
- A LibreNMS server that your computer can reach, with its API turned on.
- An account on that server that's allowed to use the API.

## Install DashyNMS

Follow the guide for your system:

- [Windows](install-windows.md)
- [macOS](install-macos.md)
- [Linux](install-linux.md)

## Sign in

1. Open DashyNMS.
2. Type your LibreNMS server's address, for example `https://librenms.example.net`.
3. Choose **Sign in with LibreNMS**.
4. Sign in on your server's own sign-in page, the same way you do in your browser.

DashyNMS then creates an API token for itself, so you don't have to. This needs LibreNMS 26.4 or later and an `https` address.

### Using an API token instead

If your server is older, or **Sign in with LibreNMS** doesn't work for you, paste an API token:

1. In LibreNMS, open the settings menu and go to **API → API Tokens**.
2. Create a token and copy it.
3. In DashyNMS, choose **Use an API token instead**, paste the token and choose **Connect**.

If your server uses a self-signed certificate, tick **Allow an untrusted or self-signed certificate**. DashyNMS shows you the certificate and asks before trusting it.

Not ready to connect to a real server? Choose **Try the demo** to look around an example network.

If you can't create tokens, ask your LibreNMS administrator. It takes them a couple of minutes.

## What happens next

DashyNMS opens on your dashboard. From here:

- **Alerts** lists everything that's open right now. A notification appears when an alert fires or clears.
- **Devices** lists every device on your network. Open one to see its ports, sensors, graphs and event log.
- **Logs** brings the event log, the alert log and Graylog together.
- **Settings** (the gear icon) is where you choose which alerts notify you, the theme, and whether DashyNMS starts when you sign in to your computer.

On Windows and macOS, closing the window leaves DashyNMS running in the notification area or the menu bar, so it can still tell you about new alerts. To quit completely, use the icon there and choose **Exit**, or **Quit DashyNMS** on a Mac. On Linux, closing the window quits DashyNMS. [Differences between Windows, macOS and Linux](platform-differences.md) explains why.

## Sign your phone in too

If you use DashyNMS Mobile, open **Settings → Server**, choose **Show** under **Sign in DashyNMS Mobile**, and scan the code with your phone's camera. Your phone asks before it signs in.

The phone uses the same API token as this computer. If you'd like to be able to cancel them separately, create a second token for the phone instead.
