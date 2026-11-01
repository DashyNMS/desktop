# Connect Graylog and Unimus

DashyNMS connects directly to these services. You need to reach them from your computer and provide credentials for each service, even if LibreNMS already uses them.

## Connect Graylog

1. Open **Settings → Graylog** and enable **Enable Graylog integration**.
2. Enter the API server address and port, for example `https://graylog.example.net`.
3. Choose the Graylog version. If your search API uses a different path, choose **Other** and enter its **Base URI**.
4. Enter the username and password. For a Graylog access token, enter the token as the username and the word `token` as the password.
5. Choose **Test connection** and resolve any error it reports.
6. Choose **Apply & Save**, then open Graylog under **Logs**.

You can copy the connection details from LibreNMS's **Settings → External → Graylog**, but credentials still need entering in DashyNMS.

### Match messages to devices

The **Query field** is the message field matched against device addresses. LibreNMS's default is `source`. Enable **Match any address** if messages identify devices by addresses beyond the hostname and primary address.

If network-wide searches work but a device has no messages, compare the message's source field with the device's addresses. Check spelling and domain names before changing the query field.

Set **Display time zone**, for example `Europe/London`, if you want Graylog's UTC timestamps shown in that zone. The device Graylog defaults for level and row count can also be changed on the device view itself.

## Connect Unimus

1. Open **Settings → Unimus**.
2. Enter the Unimus address and API token. Generate a token in Unimus under **User management → API tokens**.
3. If LibreNMS uses a `mydomain` discovery setting, enter it in **LibreNMS discovery domain** to help match devices.
4. Choose **Test connection**.
5. Apply and save, then open a device to inspect its configuration backups.

If the connection test succeeds but a device has no backup, check that Unimus has a backup for that device and that its identity matches the LibreNMS entry.

## Keep or replace stored credentials

Leave a stored password or token field blank when saving to keep the existing credential. Use **Clear** to remove it. For an internally issued certificate, use the untrusted-certificate option only after checking the certificate shown by the connection test.

See [where your data is kept](../reference/your-data.md) for local credential storage, and [troubleshooting](../reference/troubleshooting.md) if a connection fails.
