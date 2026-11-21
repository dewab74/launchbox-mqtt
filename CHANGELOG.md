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
