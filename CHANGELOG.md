# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

### Fixed

- TLS with a private CA failed when the broker certificate is issued by an
  intermediate (the presented intermediates are now used to build the chain),
  and the log now says why a certificate was rejected.
- Stopping during an in-flight publish (BigBox shutdown) was treated as a
  broker outage: it showed a "cannot reach the broker" notification and
  disconnected cleanly, which discarded the Last Will, so `offline` was never
  published.
- A launch that LaunchBox reports as launched and then exited within seconds
  (for example a missing ROM) no longer replaces "last played".
