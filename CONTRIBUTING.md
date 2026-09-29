# Contributing to Helm

Thanks for your interest in Helm. It is a personal project, so the direction is set by its maintainer, but bug reports, ideas and focused pull requests are welcome.

## Reporting bugs and asking for features

Use the [issue templates](https://github.com/huyhung1404/helm/issues/new/choose). For a bug, include the Helm version (Home or General → Updates), the platform, the steps to reproduce it and, on Windows, the relevant lines from `%LOCALAPPDATA%\Helm\logs\`. Security problems go through [SECURITY.md](SECURITY.md), never a public issue.

## Development setup

See [docs/development.md](docs/development.md) for building, running and the solution layout, and [docs/android.md](docs/android.md) for the Android app. A new tool follows every rule in [docs/new-tool-prompt.md](docs/new-tool-prompt.md).

## Pull requests

- Open an issue first for anything larger than a small fix, so the approach can be agreed before you write it.
- Keep a pull request to one change. Match the style of the surrounding code.
- `dotnet build -warnaserror` and `dotnet test` must pass; CI runs both, plus the Android build and the sync worker tests.
- Add user-visible changes under `## [Unreleased]` at the top of [CHANGELOG.md](CHANGELOG.md).
- Write commit messages as [Conventional Commits](https://www.conventionalcommits.org): `feat(vault): …`, `fix(tracker): …`, `docs: …`.

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
