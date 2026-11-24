# Where DashyNMS keeps your data

Everything DashyNMS saves stays on your computer, in your own user account. Nothing is kept in an online account.

DashyNMS saves:

- Your settings: your server's address, notification choices, dashboards, map layouts and window positions.
- Your saved passwords: your LibreNMS API token, and your Graylog and Unimus passwords if you use them. DashyNMS encrypts these, so only you, signed in to this computer, can read them.
- Your Intelligence chats, encrypted in the same way. If Codex, Claude Code, GitHub Copilot or Gemini answers a saved chat, that agent also keeps its own record of it in its own folder (`.codex`, `.claude`, `.copilot` or `.gemini` in your home folder), as it does for any conversation. A custom personality for Dashy is saved with your settings. [Intelligence](../how-to/intelligence.md#whats-sent-and-whats-kept) has the details.
- Logs of what DashyNMS did each day, kept for 14 days. They help when you report a problem.
- Downloaded updates, until they're installed.
- Map tiles, kept for a week so maps load faster.

The quickest way to find the folder is **Settings → Diagnostics → Open log folder**. Your settings are in the folder above the one that opens.

## Windows

| What | Where |
| --- | --- |
| Settings, saved passwords, chats, logs and updates | `%APPDATA%\DashyNMS` |
| Map tiles | `%LOCALAPPDATA%\DashyNMS` |
| The app itself | `%LOCALAPPDATA%\Programs\DashyNMS` |

Windows encrypts your saved passwords for your Windows account.

To open one of these folders, paste its location into the address bar in File Explorer and press **Enter**.

## macOS

| What | Where |
| --- | --- |
| Settings, saved passwords, chats, logs and updates | `~/Library/Application Support/DashyNMS/avalonia` |
| Map tiles | `~/Library/Application Support/DashyNMS/tiles` |
| The key that unlocks your saved passwords | Your login Keychain, as **DashyNMS data key** |

`~` means your home folder. To get there in Finder, choose **Go → Go to Folder**, paste the location and press **Return**.

## Linux

| What | Where |
| --- | --- |
| Settings, saved passwords, chats, logs and updates | `~/.config/DashyNMS/avalonia` |
| Map tiles | `~/.local/share/DashyNMS/tiles` |
| The key that unlocks your saved passwords | Your keyring (GNOME Keyring, KWallet or similar), as **DashyNMS data key** |
| Start when you sign in | `~/.config/autostart/net.dashynms.desktop.desktop` |

If you've set `XDG_CONFIG_HOME` or `XDG_DATA_HOME`, DashyNMS uses those folders instead of `~/.config` and `~/.local/share`.

## Starting again from scratch

**Sign out** is usually all you need. It's at the bottom of the bar down the left of the main window, and on the DashyNMS icon in the notification area or menu bar. It removes your settings, saved passwords and chats, keeps your logs and downloaded updates, and restarts DashyNMS ready to sign in again.

To remove everything, for example after uninstalling:

1. Quit DashyNMS.
2. Delete the folders listed for your system above.
   If you used Codex, Claude Code, GitHub Copilot or Gemini with Intelligence, their own records of those chats are in their folders in your home folder (`.codex`, `.claude`, `.copilot` and `.gemini`), or wherever you've pointed them with `CODEX_HOME`, `CLAUDE_CONFIG_DIR` or `COPILOT_HOME`, alongside their records of everything else you've used them for. To remove only DashyNMS's, delete those chats from inside each agent, for example with its session or history commands. Deleting a whole folder also removes the agent's sign-in and settings.
3. On macOS, open **Keychain Access**, search for **DashyNMS data key** and delete it. On Linux, run:

   ```bash
   secret-tool clear service DashyNMS account data-key
   ```

On Windows, the uninstaller offers to remove your data for you.

Edge, nightly and stable builds share this data, so switching between them keeps your settings.
