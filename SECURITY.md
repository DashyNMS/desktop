# Security policy

Thanks for helping keep DashyNMS and the people who use it safe.

## Reporting a vulnerability

Please **don't open a public issue** for a security problem.

Report it privately instead: on this repository, go to **Security → Report a vulnerability**. Only the maintainer can see the report.

It helps to include:

- what the problem is and what someone could do with it
- the steps to reproduce it, and the app version (shown in Settings → About) and your version of Windows
- anything you've found about how to fix it

## What happens next

- You'll get a reply within **5 working days** confirming the report has been received.
- We'll keep you updated while it's investigated and let you know when a fix is released.
- Once it's fixed, you're welcome to be credited in the release notes, or to stay anonymous.

Please give us a reasonable chance to release a fix before you share details publicly.

## Supported versions

Security fixes go into the **latest release** on the [Releases page](https://github.com/DashyNMS/desktop/releases), which DashyNMS offers to install for you. Please check you're on it before reporting.

## Scope

**In scope:** the DashyNMS desktop app, its installer and its updater, including:

- how it stores your server address, API token, and Graylog and Unimus credentials
- how it talks to your LibreNMS, Graylog and Unimus servers, including how it checks their certificates
- how it downloads and verifies updates

**Out of scope:**

- problems in LibreNMS itself. Please report those to the [LibreNMS project](https://github.com/librenms/librenms/security).
- problems in Graylog or Unimus themselves
- how a particular server is set up or secured
- problems that need a Windows account or computer that's already been compromised

Known, open security issues are tracked publicly once it's safe to do so.
