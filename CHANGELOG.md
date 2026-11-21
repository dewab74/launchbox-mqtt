# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/dewab74/launchbox-mqtt/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/dewab74/launchbox-mqtt/releases/tag/v0.1.0
