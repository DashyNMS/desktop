# Install on macOS

DashyNMS runs on macOS 13 Ventura or later. There's a separate download for Apple silicon Macs (M1 and later) and for Intel Macs. If you're not sure which you have, open the Apple menu and choose **About This Mac**. A Mac with Apple silicon lists a **Chip** such as "Apple M2". An Intel Mac lists a **Processor**.

## Install

1. Go to the [releases page](https://github.com/DashyNMS/desktop/releases) and download the disk image for your Mac:
   - Apple silicon: `DashyNMS-<version>-osx-arm64.dmg`
   - Intel: `DashyNMS-<version>-osx-x64.dmg`
2. Open the disk image, and drag **DashyNMS** into your **Applications** folder.
3. Eject the disk image, then open DashyNMS from Applications.
4. The first time, macOS asks whether you want to open an app downloaded from the internet. Choose **Open**.

Apple has checked release and preview downloads, so that's the only question macOS asks. Apple hasn't checked development builds from GitHub Actions. If macOS says it can't check that build for malicious software, open **System Settings → Privacy & Security**, scroll down, and choose **Open Anyway** next to DashyNMS.

Run DashyNMS from your Applications folder, not straight from the disk image. Keep the whole app together, because the network tools it uses are inside it.

### Allow notifications

macOS asks whether DashyNMS may send you notifications. Choose **Allow**, so you hear about alerts while you're working in other apps. If you said no, you can change it later in **System Settings → Notifications → DashyNMS**.

### The Keychain

DashyNMS keeps a key in your login Keychain to encrypt your API token and other saved passwords. If macOS asks whether DashyNMS can use the Keychain, choose **Always Allow** so it doesn't ask every time.

## Update

DashyNMS checks for new versions by itself. When one is ready, choose **Relaunch**, and DashyNMS replaces itself and opens again. Your settings and sign-in stay as they are.

This works when DashyNMS is in **Applications** or in **Applications** inside your home folder. If yours is somewhere else, or your Mac is managed by your organisation, download the new disk image and drag it into Applications to replace the old copy.

## Uninstall

1. Quit DashyNMS. Click its icon in the menu bar and choose **Quit DashyNMS**.
2. Drag **DashyNMS** from Applications to the Bin.

That leaves your settings and saved sign-in behind, in case you install it again. [Where DashyNMS keeps your data](../reference/your-data.md#macos) explains how to remove them too.
