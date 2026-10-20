# DashyNMS

WPF/.NET 9 desktop client for LibreNMS. `src/DesktopNMS` is the app and
`src/DesktopNMS.Core` is the shared code (DashyNMS Mobile builds it too).
Building, layout and storage are covered in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Commits and pull requests

- Work on a branch, open a PR into `master` (one issue per PR), and squash-merge.
- The PR title is the commit on `master`, so it must follow the naming standard:
  `type(scope): what changes, as the user would notice it`.
  - Types: `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`, `revert`.
  - Scope: the app area, in lower case, for example `dashboard`, `alerts`,
    `device-details`, `logs`, `graylog`, `settings`, `intelligence`, `core`,
    `installer`. Separate two with a comma; leave it out if there's no single area.
  - Lower-case start, no full stop, under about 72 characters, plain words,
    describing the result rather than the code: `fix(device-details): neighbours load for ping-only devices`.
  - No issue numbers in the title. The PR description starts with `Closes #NN`.
  - A breaking change to the settings format or Core's public API: `feat(core)!: …`.
- Branch commits use the same format.
- The full standard and more examples are under
  [Commits and pull requests](docs/DEVELOPMENT.md#commits-and-pull-requests) in the development docs.
