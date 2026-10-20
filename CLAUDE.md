# DashyNMS

WPF/.NET 9 desktop client for LibreNMS. `src/DesktopNMS` is the app. The shared code (`DesktopNMS.Core`) lives in
[DashyNMS/core](https://github.com/DashyNMS/core) and comes in as the `DashyNMS.Core`
package, pinned to a development build between releases. Make Core changes
there, and set `DASHYNMS_LOCAL_CORE` to build against a local checkout.
Building, layout and storage are covered in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Commits and pull requests

- Work on a branch, open a PR into `master` (one issue per PR), and squash-merge.
- The PR title is the commit on `master`, so it must follow the naming standard:
  `type(scope): what changes, as the user would notice it`.
  - Types: `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`, `revert`.
  - Scope: the app area, in lower case (full list in the development docs), for example `dashboard`, `alerts`,
    `device-details`, `logs`, `graylog`, `settings`, `intelligence`, `core`,
    `installer`. Separate two with a comma; leave it out if there's no single area.
  - Lower-case start, no full stop, under about 72 characters, plain words,
    describing the result rather than the code: `fix(device-details): neighbours load for ping-only devices`.
  - No issue numbers in the title. The PR description starts with `Closes #NN`.
  - A breaking change to the settings format or Core's public API: `feat(core)!: …`.
- Branch commits use the same format.
- Issue titles use the same form too, worded as the result the fixing PR delivers. Epics end with `(epic)`.
- No AI attribution or `Co-Authored-By` lines in commits, PRs or release notes.
- The full standard and more examples are under
  [Commits, pull requests and issues](docs/DEVELOPMENT.md#commits-pull-requests-and-issues) in the development docs.
