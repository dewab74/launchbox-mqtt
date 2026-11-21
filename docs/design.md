# Design: LaunchBox/BigBox → MQTT plugin

Status: **milestones 1–5 done** (2026-11-20; v0.1.0 and v0.2.0 released): repo,
baseline, CI, `LaunchBoxMqtt.Core` with tests, the plugin shell verified end to
end on a real cabinet (LaunchBox/BigBox 14.1.0.5 → Home Assistant Mosquitto
over TLS → discovered entities), and a settings window verified on the same
cabinet. Written as a plan on 2026-11-20 and kept current here; update it when
the design changes.

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

### Plugin shell (milestone 2) and what the real host does

Verified on BigBox 14.1.0.5 on 2026-11-20 (the plugin log records every event):

- **Loading:** a manifest-less plugin is a folder under `LaunchBox\Plugins\`
  with the DLLs. LaunchBox does **constructor injection** for it:
  `LaunchBoxMqttPlugin(IPluginFileSystemStore, IPluginLogger, IPluginSecretStore,
  INotificationManager)` loads fine. (`IPluginPaths` works too but is obsolete.)
- **Instances:** LaunchBox constructs the entrypoint class **once per plugin
  interface** (`PluginInitialized` arrives twice), so all state lives in the
  static `PluginHost`, created once per process.
- **Data folder:** `IPluginFileSystemStore.RootDirectory` is
  `LaunchBox\Local\Plugins\.data\LaunchBoxMqtt\files`. The settings file and
  the log live there.
- **Secret store** works and persists across restarts (password migrated from
  the settings file, then read back on the next start with the file
  password-free).
- **Events seen:** `PluginInitialized`, `BigBoxStartupCompleted`,
  `SelectionChanged`, `OnBeforeGameLaunching` → `GameStarting` →
  `OnAfterGameLaunched` (≈0.8 s later for MAME), `GameExited` →
  `OnGameExited`, `BigBoxShutdownBeginning`. There is **no attract-mode
  event**; attract mode (and the lock, as a backstop to its events) is polled
  from `IStateManager` every 2 s, which caught attract mode on the cabinet.
- **Failed launches:** contrary to the forum report, a launch with a missing ROM
  *does* raise `OnAfterGameLaunched` and then `OnGameExited` ≈0.5 s later. So a
  session shorter than `MinimumSessionLength` (10 s) is not recorded as "last
  played". The `launching` timeout stays for launches that never report back.
- **Steam and other launcher-based games:** LaunchBox's own API docs say
  `OnGameExited` can fire right after launch when the launched process exits
  (Steam). Such games show as idle within seconds and are not recorded. Fixing
  that needs a process watch; not planned unless users ask.
- **Dependencies:** the plugin ships its own `MQTTnet.dll` and
  `LaunchBoxMqtt.Core.dll`; LaunchBox has no copy of MQTTnet and loads them
  without conflict. `System.Text.Json` comes from LaunchBox's runtime.
- **Shutdown:** `BigBoxShutdownBeginning` publishes the final state, then
  `offline`, then disconnects cleanly (bounded to 2 s). Verified on the wire.
  A first version treated the publish cancelled by shutdown as a broker outage,
  disconnected cleanly and so lost the Last Will (fixed, with a test).
- **Notifications:** `INotificationManager` is unavailable in BigBox (per its
  docs), so connection problems there are log-only.
- **TLS:** the homelab broker presents leaf + intermediate. Custom-CA validation
  must use the intermediates the TLS stack received, not just the leaf (fixed,
  with a test); rejections now log the reason.

### Settings UI (milestone 3)

- LaunchBox 14.1 has a host-rendered settings form API
  (`IPluginConfigurationFormsCapability`), shown in **Tools › Manage ›
  Plugins**. That list contains only plugins with a `manifest.json`
  (`SchemaVersion: 2`, installed under `LaunchBox\System\Plugins\`), and the
  manifest component types are all providers (`storefront-provider/1`,
  `emulator/1`, `playlist-provider/1`, `achievement-provider/1`,
  `game-data-source/1`, `game-discovery-source/1`, `game-activity-source/1`,
  `software/1`). There is none for an event plugin, and the host has a
  "not authorized for manifest-less plugins" policy. A manifest-less plugin
  implementing the capability loads but is not listed (checked on the cabinet
  with the "Installed" filter), so that code was removed rather than shipped
  unverifiable.
- Instead: `ISystemMenuItemPlugin` adds **LaunchBox MQTT settings…** to
  LaunchBox's Tools menu (not BigBox's System menu: BigBox is
  controller-driven). It opens a WinForms window, laid out in code, so the
  plugin targets `net10.0-windows` (`EnableWindowsTargeting` keeps it building
  on macOS/Linux).
- All form logic is in Core (`SettingsForm`: field ids, values ↔ settings,
  per-field validation, "blank password keeps the stored one";
  `SettingsFile.Save` writes, then reads back to check). The plugin's
  `SettingsActions` does load, save (password → secret store, read back; file;
  live bridge reload) and test (own client id, 10 s timeout).
- Verified on the cabinet: values load; Test connection succeeds ("MQTT 5")
  and fails clearly for a bad host ("No such host is known"); Cancel leaves the
  file untouched; Save rewrites the file without the password and restarts
  the bridge (offline → online in 70 ms on the wire).
- Seen once: on one LaunchBox desktop start the first connect took 26 s (all
  other starts 0.1–0.3 s; no error logged). Possibly thread-pool pressure while
  LaunchBox starts. Not reproduced; watch for it.

### Commands (milestone 5)

- LaunchBox 14.1 offers `DataManager.GetGameById` and
  `BigBoxMainViewModel.PlayGame` / `LaunchBoxMainViewModel.PlayGame` (the full
  launch with startup screens). The plugin passes null for the additional app,
  emulator and command line, so a command can only start a library game the
  normal way. `PlayGame` runs on LaunchBox's UI thread via the
  `SynchronizationContext` captured when LaunchBox constructs the plugin.
- There is **no API to exit a game**. `exit_game` sends WM_CLOSE
  (`Process.CloseMainWindow`, like Alt+F4) to windowed processes that descend
  from the Big Box process (Toolhelp parent ids), skipping LaunchBox's helpers
  under `Core\` and `System\`. Parentage matters: on the reference cabinet,
  Game Bar and Edge processes start whenever a game launches. Nothing is ever
  killed; a game that ignores the close is reported, not forced.
- There is **no toast in Big Box**: `INotificationManager` documents that Big
  Box has no notification system. A custom topmost window over fullscreen
  emulators was judged too fragile; dropped from scope.
- Core: `CommandParser` (strict: two names, GUID only, ≤256 bytes, UTF-8,
  retained → refused), bridge rules (off unless `CommandsEnabled`; subscribe on
  every connect with retain-handling "do not send"; one at a time; ≥3 s apart;
  30 s timeout; outcome on `command_result`, not retained), discovery adds a
  button and a text entity, or empties their configs so they disappear.

### Game stats: what LaunchBox exposes (probe, 2026-11-20)

A temporary read-only probe (commit cc60459, removed before v0.2.0) logged
what the 14.1 services return on the reference cabinet. Inputs for a future
"game stats" milestone:

- `IAchievementManager` (constructor injection): sources `retroachievements`,
  `steam`, `gog`. RetroAchievements has an account with hardcore and softcore
  contexts; supports catalogs, progress and library progress, not availability
  checks. `GetCachedAsync(IGame)` for a Genesis game returned the RA game
  (id 54) with `Status: NotLoaded`: nothing cached until `RefreshAsync`, which
  goes to the network through LaunchBox's rate limits. Arcade games had no
  association.
- `ILeaderboardManager`: sources `mame` and `retroachievements`. The `mame`
  source is keyed by **ROM name** (`gyrussce`), not the LaunchBox game id, and
  returns community entries with `Rank`, `Score`, `InGameName`, `RecordedAt`
  (Gyruss top: 448,000). `retroachievements` leaderboards answer
  `Unsupported` for these games. The player's own MAME rank needs LaunchBox's
  `UploadMameCommunityHighScores`, which shares scores publicly (off on the
  reference cabinet).
- `IPlayerActivityManager`: source `retroachievements`, recent games with
  recent unlocks (the probe's JSON dump hit a reference cycle in
  `AdditionalData`; map fields explicitly).
- No API passes a game *score* to a plugin. MAME's own `hiscore` plugin saves
  raw `.hi` files (decoding needs hi2txt), out of scope.
- `EnableMameGameEvents` (LaunchBox setting, off on the reference cabinet)
  loads LaunchBox's `lbevents` Lua plugin into MAME, which reports
  `MameGameSessionStarted`, `MameCoinInserted`, `MamePlayerStarted`,
  `MameGameSessionEnded` (no payload reaches `ISystemEventsPlugin`). Untested
  with MAME 0.233, and its interaction with MAME's other plugins (hiscore) is
  unverified.

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
  - The plugin project references the DLL with `Private=false` from
    `.launchbox-ref/` at the repo root (gitignored; copy it from a LaunchBox
    install), else `$(LaunchBoxDir)\Core` (default `C:\LaunchBox`). Without
    it the project compiles nothing and prints `SKIPPED`, so Core builds and
    tests everywhere; `-p:RequireLaunchBoxApi=true` (used by
    `scripts/package-plugin.sh`) makes it an error. It targets `net10.0` and
    builds on macOS too.
  - `scripts/package-plugin.sh` fails if any `Unbroken.*` assembly lands in the
    package.
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
2. Plugin shell: launch/exit events and availability, tested on a real cabinet. **Done 2026-11-20.**
3. HA discovery and settings UI with the secret store. Tag v0.1.0. **Done 2026-11-20**, released.
4. Reference deployment (above). **Done 2026-11-20:** v0.1.0 installed by
   the cabinet's installer (pinned by version and SHA256), Home Assistant's
   now-playing chip switched to the plugin's entities, the SSH poll removed.
5. v2 commands, opt-in. **Done 2026-11-20**, released in v0.2.0 (exit game,
   launch game; no Big Box toast: no API).
6. Possible: game stats (RetroAchievements progress, MAME community
   leaderboard top score, MAME coin/start events). See "Game stats" above.

## Decisions (settled 2026-11-20)

- Repo: `dewab74/launchbox-mqtt` (public, personal account).
- License: MIT.
- v2 commands: in scope, opt-in and off by default, after v1 (milestone 5).

## Open questions

- Release build in CI: a stub reference assembly would allow CI-built
  releases; for now `scripts/release.sh` builds locally.
- If LaunchBox and BigBox run at the same time on one PC, both processes load
  the plugin with the same MQTT client id and keep disconnecting each other.
  Decide: per-process client id with one designated publisher, or document
  "one at a time".
- LaunchBox may open its plugin settings API to manifest-less or third-party
  manifest plugins later; then the host-rendered form can replace the window.
