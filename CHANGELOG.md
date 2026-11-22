# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.0] - 2026-11-22

### Added

- `LaunchBoxMqtt-Setup-<version>.exe`, an Inno Setup installer. The
  `installer` workflow builds it from each release's zip and attaches it,
  with its SHA256 and a provenance attestation. It asks for the LaunchBox
  folder, refuses to run while LaunchBox or BigBox is open, and runs
  silently (`/DIR=`, plus optional `/HOST=` … switches that write the
  settings).
- `verifyCertificate` (default `true`) and a matching checkbox in the
  settings window. With TLS, turning it off accepts the broker's certificate
  without checking it, for brokers with a self-signed certificate. The
  connection is still encrypted, and the log line notes it.

### Changed

- Examples and tests use a device named "Arcade PC" (node id `arcade_pc`).

## [0.4.0] - 2026-11-21

### Added

- Opt-in game stats (`statsEnabled`, off by default), published as retained
  JSON on `launchbox/<node>/stats` with two Home Assistant sensors:
  "Achievements" (RetroAchievements progress through LaunchBox's achievement
  manager; points filled from the catalog when the summary has none) and
  "High score" (this PC's own MAME high score table, decoded by LaunchBox's
  bundled hi2txt, factory defaults removed, top 5 as attributes; never an
  online leaderboard). Read on launch and again a few seconds after the game
  exits, off LaunchBox's thread with a 45 s limit; republished on reconnect;
  cleared and removed when turned off.
- Settings window: "Publish game stats".
- A second entry point (`GameStatsPlugin`) receives LaunchBox's achievement
  service, so a LaunchBox without it only loses achievements.

## [0.3.0] - 2026-11-20

### Added

- Opt-in artwork (`artworkEnabled`, off by default): on launch, the game's
  arcade marquee, else clear logo, else box front, scaled to
  `artworkMaxWidth` (default 800 px) and published as PNG, retained, on
  `launchbox/<node>/artwork`, with a Home Assistant MQTT image entity
  "Artwork". Read and scaled off LaunchBox's thread; republished on
  reconnect; cleared and removed when turned off.
- Settings window: "Publish game artwork" and "Artwork max width".

### Fixed

- `launch_game` could not launch games whose LaunchBox id is stored in
  uppercase ("no game with id … in the LaunchBox library"): LaunchBox's
  lookup is case-sensitive. Ids now match in any case.

## [0.2.0] - 2026-11-20

### Added

- Opt-in commands (`commandsEnabled`, off by default): `launch_game` with a
  LaunchBox game id, through LaunchBox's own launch, and `exit_game`, which
  asks the processes Big Box started to close and never kills them. Retained
  command messages are ignored. One command at a time, at least 3 s apart,
  30 s timeout. Outcomes are published on `command_result`. With commands on,
  Home Assistant discovery adds an "Exit game" button and a "Launch game (id)"
  text entity, and removes them again when commands are turned off.
- "Accept commands" in the settings window.
- Retained command messages are refused even when delivered live (MQTT 5
  retain-as-published).

### Fixed

- A broker integration test could fail with "Address already in use" when its
  port was taken between choosing it and binding it.

## [0.1.0] - 2026-11-20

### Added

- `LaunchBoxMqtt.Core`: state tracking for game launch/exit (including the
  unconfirmed-launch timeout), BigBox running/attract/locked state, the JSON
  state payload, Home Assistant MQTT discovery configs, and an MQTTnet
  transport with Last Will availability, MQTT 5 with 3.1.1 fallback, and TLS
  with a private CA.
- Background publisher that never blocks LaunchBox events, coalesces state,
  and reconnects with jittered backoff.
- Unit tests and in-process broker integration tests.
- `LaunchBoxMqtt.Plugin`: the LaunchBox/BigBox plugin shell (game launch/exit,
  BigBox start/stop and lock events, attract mode and lock polled every 2 s),
  using LaunchBox 14's injected logger, file store, secret store and
  notifications.
- Settings file `launchbox-mqtt.json` in the plugin data folder, with a starter
  file written on first run; a password written there is moved into
  LaunchBox's secret store and removed from the file.
- Plugin log file `launchbox-mqtt.log` with every received event.
- `scripts/package-plugin.sh`: builds the plugin zip and SHA256, and fails if
  LaunchBox's own assemblies would be shipped.
- Settings window in LaunchBox (**Tools › LaunchBox MQTT settings…**) with
  **Test connection** (connects with the typed values under a separate client
  id, without saving). Saving applies the settings at once, without restarting
  LaunchBox. The password goes only to LaunchBox's secret store.
- `scripts/release.sh`: tags and publishes a release, then re-downloads the
  assets to check them.

[Unreleased]: https://github.com/dewab74/launchbox-mqtt/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/dewab74/launchbox-mqtt/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/dewab74/launchbox-mqtt/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/dewab74/launchbox-mqtt/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/dewab74/launchbox-mqtt/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/dewab74/launchbox-mqtt/releases/tag/v0.1.0
