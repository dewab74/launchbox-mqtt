# Design: LaunchBox/BigBox → MQTT plugin

Status: **milestone 1 done** (2026-11-20): repo, baseline, CI, and
`LaunchBoxMqtt.Core` with tests. Written as a plan on 2026-11-20 and kept
current here; update it when the design changes.

## Why

Nothing maintained exists (checked 2026-11-20). The forum "Launchbox MQTT"
plugin by srxz (v2.0.3) has no license, says support is "super limited", and
has one report of games failing to launch. Its GitHub source,
`rodrigosiviero/launchbox_mqtt`, is a single day of commits (2025-02-16) with
prebuilt DLLs under `bin/Debug/` and does not match the released build. No
other GitHub project publishes LaunchBox state to Home Assistant.

The usual workaround is polling the PC (for example an SSH status script every
30 s). The plugin replaces that with an event-driven feed that any LaunchBox
user can install.

## Product

**Name:** `launchbox-mqtt`, public at
[`dewab74/launchbox-mqtt`](https://github.com/dewab74/launchbox-mqtt), MIT.

A LaunchBox/BigBox plugin that publishes front-end and game state to MQTT, with
**Home Assistant MQTT discovery**: install it, enter the broker details, and a
"LaunchBox (<PC name>)" device with its entities appears in HA without any YAML.

### v1 scope (state out)

| Entity (HA discovery) | Source | Notes |
| --- | --- | --- |
| `sensor` Now playing | `IGameLaunchingPlugin` | Title, or `Idle`. Attributes: platform, emulator, developer, publisher, genres, release year, play count, LaunchBox game id, start time |
| `binary_sensor` Playing | launch / exit events | `OnAfterGameLaunched` → on, `OnGameExited` → off |
| `sensor` Platform | launch event | |
| `binary_sensor` BigBox running | `ISystemEventsPlugin` + `IStateManager.IsBigBox` | |
| `binary_sensor` Attract mode | `IStateManager.IsBigBoxInAttractMode` | Poll on a timer if there is no event for it |
| `binary_sensor` BigBox locked | `IStateManager.IsBigBoxLocked` | No `lock` device class: in HA that class means ON = unlocked |
| `sensor` Last played | exit event | Title; the session (id, platform, start, end, length) as attributes |
| `sensor` Last played at | exit event | `timestamp` device class |
| `sensor` Last session length | exit event | `duration`, seconds. Computed in the plugin, so HA does not need statistics |
| availability | MQTT Last Will | `online` / `offline`, so HA shows the device unavailable when LaunchBox exits or the PC dies |

### v2 scope (commands in), opt-in and off by default

- Exit the running game and return to BigBox.
- Launch a game by LaunchBox id (`ILaunchBoxMainViewModel.PlayGame`; check
  what BigBox offers).
- Show a toast in BigBox (`INotificationManager`).

Commands accept a game id only, never a path or command line, so a broker user
cannot make the PC run arbitrary programs.

## Platform facts (checked on a reference cabinet, 2026-11-20)

- LaunchBox/BigBox **14.1.0.5**, running on **.NET 10**
  (`BigBox.runtimeconfig.json`: `net10.0`, WindowsDesktop 10.0.14).
  `Unbroken.LaunchBox.Plugins` 14.1.0.5. `C:\LaunchBox\Plugins` is empty.
- Plugin interfaces used: `IGameLaunchingPlugin` (`OnBeforeGameLaunching`,
  `OnAfterGameLaunched`, `OnGameExited`), `ISystemEventsPlugin`
  (`OnEventRaised`), `IStateManager`, `IDataManager`.
- New in 14.x and worth using:
  - `IPluginSecretStore` holds the MQTT password (not a plaintext config file).
  - `IPluginConfigurationFormsCapability` / `IPluginConfigurationValues`
    provide the settings UI.
  - `IPluginLogger` for logging; `INotificationManager` for errors shown to the
    user.
- Known quirks from the forum: `OnAfterGameLaunched` fires at once for some
  Steam titles because the launcher process exits; `OnGameExited` does not fire
  when a launch fails. The plugin needs a launch-failure path
  (`OnBeforeGameLaunching` sets a flag, `OnAfterGameLaunched` clears it).

### Implemented in Core (milestone 1)

- `StateTracker`: thread-safe state machine, `idle → launching → playing →
  idle`. A session is recorded only for a confirmed launch. An unconfirmed
  launch expires to `idle` after `BridgeOptions.LaunchTimeout` (2 min), which
  is the launch-failure path. BigBox exit also clears attract mode and lock.
- `StatePayload`: one retained JSON document on the state topic; every HA
  entity reads it through a value template. Field names are a public contract
  (README "Topics"). A test checks that every `value_json.<field>` in the
  discovery templates exists in the payload.
- `DiscoveryBuilder`: per-component discovery configs sharing one device,
  availability topic and `origin` block; `BuildRemovals()` empties them (for
  uninstall / discovery off; not wired yet).
- `MqttNetTransport`: MQTTnet 5, QoS 1, retained, Last Will `offline`, MQTT 5
  with 3.1.1 fallback (MQTTnet 5 reports the refusal as an exception whose
  message names `UnsupportedProtocolVersion`; an integration test pins this),
  TLS with an optional private-CA PEM bundle. The custom CA only re-evaluates
  chain errors; a host-name mismatch is always rejected.
- `LaunchBoxBridge`: the plugin shell's only entry point. `On*` methods update
  memory and signal a background loop; they never block, touch the network or
  throw (tested: 2000 events with a hung broker return in well under 500 ms).
  State is coalesced, not queued: after an outage the broker gets the current
  state. On every (re)connect it publishes discovery, state, then `online`.
  Reconnect uses jittered exponential backoff (1 s → 60 s). One user
  notification per outage. `StopAsync(timeout)` publishes `offline` and
  disconnects cleanly, giving up after the timeout (the Last Will covers it).

## Architecture

```text
src/
  LaunchBoxMqtt.Core/      net10.0, no LaunchBox reference: state model,
                           payload + HA discovery builders, MQTT client wrapper
  LaunchBoxMqtt.Plugin/    net10.0-windows, references Unbroken.LaunchBox.Plugins:
                           thin adapters from LaunchBox events to Core
tests/
  LaunchBoxMqtt.Core.Tests/ xUnit, runs on macOS/Linux/CI
```

- Keep Core free of LaunchBox so tests run anywhere and the plugin shell
  stays small.
- **MQTT:** MQTTnet (MIT, maintained), MQTT 5 with 3.1.1 fallback, TLS with a
  custom CA (common for home brokers behind a private CA), retained state
  topics, Last Will for availability.
- **Topics:** `launchbox/<pc>/state`, `launchbox/<pc>/availability`,
  `launchbox/<pc>/command/...` (v2), and
  `homeassistant/<component>/launchbox_<pc>/<object>/config` for discovery.
  Topic prefix and discovery prefix are configurable.
- **Failure modes:** a dead broker must never block or slow a game launch. All
  publishing is async on a background queue, events are fire-and-forget into
  the queue, and the plugin reconnects with backoff. Failures are logged and
  shown once as a notification, not on every event.
- <a id="reference-assembly"></a>**Reference assembly:**
  `Unbroken.LaunchBox.Plugins.dll` belongs to LaunchBox and is not committed
  (`.gitignore` also blocks it). **There is no public NuGet package** for it
  (checked 2026-11-20: nuget.org search for "launchbox" and "Unbroken" returns
  nothing, and `unbroken.launchbox.plugins` is not in the flat container). So:
  - The plugin project (milestone 2) references the DLL from
    `$(LaunchBoxDir)\Core` (default `C:\LaunchBox`) with `Private=false`, and
    is only added to the build when that file exists, so Core still builds and
    tests everywhere.
  - CI builds and tests Core only (Ubuntu and Windows).
  - Releases: either build the plugin zip locally and upload it with its
    checksum, or (to decide in milestone 3) compile in CI against a small
    stub assembly with the same name and the few interface signatures used.
    That needs a check that LaunchBox binds a stub-compiled plugin, and that
    reproducing the interface signatures is acceptable.

## Coding practices (maintainer baseline + .NET)

From the new-repo baseline (applied with the skill's `bootstrap_repo.sh` with
`markdown commit-msg`, plus a local C# fragment it does not have yet):

- `pre-commit` installed and documented in the README "Development setup".
  - Universal hooks.
  - gitleaks: `.gitleaks.toml` MUST have `[extend] useDefault = true`, or it
    scans with zero rules.
  - detect-secrets with a committed `.secrets.baseline`.
  - markdownlint.
  - C#: local `dotnet format --verify-no-changes` and `dotnet build -warnaserror`
    hooks.
- Lint and leak scanning run locally. The only CI job is the backstop
  (`pre-commit run --all-files`), plus build and test, plus release.
- `.editorconfig` with the .NET analyzer and naming rules. `Directory.Build.props`:
  `Nullable=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`,
  `EnforceCodeStyleInBuild`, deterministic builds, NuGet lock files (locked
  restore in CI). Central package versions in `Directory.Packages.props`.
- `global.json` pins SDK 10.0.100 with `rollForward: latestMajor` and selects
  Microsoft.Testing.Platform (xUnit v3) for `dotnet test`.
- gitleaks: the baseline's blanket allowlist for `tests/` paths was removed, so
  test code is scanned too.
- `.gitignore` for .NET (`bin/`, `obj/`, `*.user`, `.vs/`): no committed
  binaries, unlike the forum repo.

Public-product extras:

- License **MIT** (MQTTnet is MIT too).
- `README` covering install, broker setup, HA example, topic reference and
  troubleshooting. Also `CHANGELOG.md` (Keep a Changelog), `SECURITY.md`,
  `CONTRIBUTING.md`, and issue/PR templates.
- SemVer tags. The release workflow (milestone 3) builds the plugin zip with a SHA256
  checksum, attaches it to a GitHub Release, and states the LaunchBox versions
  it was tested against.
- Dependabot for NuGet and GitHub Actions. Branch protection on `main`.
- Conventional Commits.

## Reference deployment (after v1 ships)

The maintainer's own cabinet is the first deployment: a dedicated broker user,
the release zip installed with its checksum verified, the polled status sensor
replaced by the discovered entities, and automations such as dimming the room
lights while Playing, a play-time history, and a BigBox toast ("dinner in
10"). Its specifics live in the maintainer's private infrastructure repo.

## Milestones

1. Repo scaffold with the baseline, CI backstop, and Core with tests (no LaunchBox). **Done 2026-11-20.**
2. Plugin shell: launch/exit events and availability, tested on a real cabinet.
3. HA discovery and settings UI with the secret store. Tag v0.1.0.
4. Reference deployment (above).
5. v2 commands, opt-in.

## Decisions (settled 2026-11-20)

- Repo: `dewab74/launchbox-mqtt` (public, personal account).
- License: MIT.
- v2 commands: in scope, opt-in and off by default, after v1 (milestone 5).

## Open questions for milestone 2

- Is there an `ISystemEventsPlugin` event for attract mode and lock, or does the
  shell poll `IStateManager` on a timer?
- The Steam quirk: `OnAfterGameLaunched` fires at once because the launcher
  exits. Does `OnGameExited` then also fire early? If so, a minimum session
  length or a process watch may be needed. Check on a real install.
- Does the plugin's own `MQTTnet.dll` load cleanly next to LaunchBox's
  assemblies (no conflicting copy in `LaunchBox\Core`)?
