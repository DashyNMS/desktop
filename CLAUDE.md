# DashyNMS

Avalonia/.NET 9 desktop client for LibreNMS on Windows, macOS and Linux. `src/DesktopNMS.Avalonia` is the app; `src/DesktopNMS.Presentation` holds the view models and services with no UI framework in them. UI and platform-specific adapters stay in the Avalonia project behind interfaces. The WPF project has been retired.

- Namespaces remain `DesktopNMS`, and XAML refers to Presentation types with `;assembly=DesktopNMS.Presentation`.
- Shared code lives in [DashyNMS/core](https://github.com/DashyNMS/core) and comes in as the `DashyNMS.Core` package, pinned to a nightly or edge package between releases in `Directory.Build.props`. Make Core changes there; set `DASHYNMS_LOCAL_CORE` to build against a local checkout.
- Use sealed classes and XML doc comments explaining why.

## Repository conventions

- Before changing build, test or publication workflows, read [the developer guide](docs/DEVELOPMENT.md). Before changing release notes, read its [Release notes](docs/DEVELOPMENT.md#release-notes) section.
- Before changing shared conventions or security checks, read [Repository standards](docs/REPOSITORY-STANDARDS.md); update the canonical source and every affected consumer.
- Create or find an issue describing the bug or intended change before opening any PR, including dependency, documentation and CI changes. Every PR must reference its issue.
- When filing an issue, set its organisation type, relevant labels and milestone. Follow [issue categorisation](https://github.com/DashyNMS/.github/blob/main/docs/REPOSITORY-STANDARDS.md#issue-categorisation) for milestone choices and API verification.
- Work on a branch; use one issue per PR into `main`, then squash-merge. The PR title becomes the commit subject on `main`.
- PR titles, branch commits and issue titles use `type(scope): what changes, as the user would notice it`. Read [Commits, pull requests and issues](docs/DEVELOPMENT.md#commits-pull-requests-and-issues) for this repository's types, scopes and examples.
- Start the description in lower case, omit the final full stop and aim for a complete subject under 72 characters. Separate two scopes with a comma; omit the scope when there is no single area. Document a new scope in the developer guide in the same PR.
- Word issue titles as the result the fixing PR delivers. Epics end with `(epic)`. Keep issue numbers out of titles; start the PR body with `Closes #NN` when resolving an issue, then explain what changes and how it was checked.
- Mark breaking changes with `!` and explain the migration and version implications for affected consumers.
- Dependabot uses `build(deps): ...` for every ecosystem. Authenticated Dependabot may use generated capitalisation and exceed 72 characters.
- Use British spelling in user-facing text. Keep real device names, hostnames, addresses, credentials and other identifiable network data out of issues, PRs, commits, logs, screenshots and fixtures. Use `example.net` and documentation address ranges.
- Commits, PRs and release notes carry no AI attribution or `Co-Authored-By` lines.
- Read `LICENSE` before changing or redistributing repository material. Preserve third-party licences, copyright notices and provenance.

## Validation and releases

- Building, tests, layout and storage are covered in [the developer guide](docs/DEVELOPMENT.md). Validate affected desktop platforms and report unavailable checks.
- A change users would notice adds or updates its line in `docs/releases/<version>.md`, using the version in `Directory.Build.props`, in the same PR. The app embeds it as What's New; GitHub release notes come from that file.
- Changes to app behaviour also update `docs/guide` in the same PR. Before editing the guide or wiki publishing, read [User guide and wiki](docs/DEVELOPMENT.md#user-guide-and-wiki).
