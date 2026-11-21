# Contributing

Thanks for helping. Issues and pull requests are welcome.

## Setup

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (or newer)
   and [pre-commit](https://pre-commit.com/).
2. Clone, then install the git hooks. Hooks live in `.git/hooks/`, which is not
   cloned, so this is needed once per clone:

   ```bash
   pre-commit install
   pre-commit run --all-files
   ```

3. Build and test:

   ```bash
   dotnet build LaunchBoxMqtt.slnx
   dotnet test --solution LaunchBoxMqtt.slnx
   ```

`LaunchBoxMqtt.Core` and its tests build on Windows, macOS and Linux. The
plugin project also builds on any OS once LaunchBox's
`Unbroken.LaunchBox.Plugins.dll` is in `.launchbox-ref/` (copied from
`<LaunchBox>\Core\`; gitignored, never commit it). Without it the plugin
project is skipped and only Core builds; see
[docs/design.md](docs/design.md#reference-assembly). Package with
`scripts/package-plugin.sh <version>`.

## Rules

- **Conventional Commits** (`feat: ...`, `fix: ...`, `docs: ...`). The
  commit-msg hook enforces it; the changelog is built from it.
- The hooks run `dotnet format --verify-no-changes`, `dotnet build -warnaserror`,
  markdownlint, gitleaks and detect-secrets. Do not bypass them with
  `--no-verify`. If a secret scanner fires on a real credential, rotate it.
- Never commit LaunchBox's assemblies or any `bin/`/`obj/` output.
- New behaviour comes with tests. A LaunchBox event handler must never block or
  throw: put logic in Core, where it can be tested, and keep the plugin shell
  thin.
- Update `CHANGELOG.md` under `[Unreleased]`.
