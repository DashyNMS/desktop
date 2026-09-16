# Contributing to DashyNMS

Thanks for the interest. This is a small, mostly-solo project, so the
process is deliberately lightweight.

## Finding something to work on

Planned work lives as [GitHub issues](https://github.com/DashyNMS/desktop/issues),
grouped under the [1.0.0](https://github.com/DashyNMS/desktop/milestone/1),
[1.1.0](https://github.com/DashyNMS/desktop/milestone/2) and
[1.2.0](https://github.com/DashyNMS/desktop/milestone/3) milestones, each
labelled `Feature`, `UI/UX`, `Performance`, or `bug`. If you want to work on
something not already tracked, open an issue first so the approach can be
agreed before you spend time on it - especially for anything touching the
API layer or view-model architecture described in the [README](README.md).

## Making a change

1. Fork the repo and branch from `master`.
2. See the README's [Building and running](README.md#building-and-running)
   section for the build/test commands - the same `dotnet build` /
   `dotnet test` steps run in CI on every PR.
3. Open a PR against `master`. Reference the issue it addresses (e.g.
   `Closes #42`) so it closes automatically on merge.
4. The "Build and test" check must pass before a PR can merge. If your PR's
   first-ever workflow run on this repo doesn't start, that's expected - a
   maintainer has to approve Actions runs for a first-time contributor.

## Conventions

- No unrelated changes bundled into a feature PR - keep it to one issue's
  worth of scope.
- Match the existing code style (see the ViewModels/Services already in the
  repo) rather than introducing a new pattern for the same kind of problem.
- New LibreNMS API surface goes behind an interface in
  `DesktopNMS.Core/Api`, grouped by resource - see the "Why the API layer
  looks like this" section in the README.
