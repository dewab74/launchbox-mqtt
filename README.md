# launchbox-mqtt

[![build and test](https://github.com/dewab74/launchbox-mqtt/actions/workflows/ci.yml/badge.svg)](https://github.com/dewab74/launchbox-mqtt/actions/workflows/ci.yml)
[![pre-commit backstop](https://github.com/dewab74/launchbox-mqtt/actions/workflows/pre-commit.yml/badge.svg)](https://github.com/dewab74/launchbox-mqtt/actions/workflows/pre-commit.yml)

A [LaunchBox / BigBox](https://www.launchbox-app.com/) plugin that publishes
what your arcade or game PC is doing to MQTT, with
[Home Assistant MQTT discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery).

Install it, enter your broker details, and a **LaunchBox (&lt;PC name&gt;)**
device appears in Home Assistant with "Now playing", "Playing", platform,
BigBox and attract-mode entities, all without any YAML. It is event-driven:
state is published when LaunchBox raises an event, not polled.

## Status

**Early development, not yet released.** The plugin works end to end on
LaunchBox/BigBox 14.1.0.5 (tested on a real cabinet against Home Assistant's
Mosquitto broker over TLS). Setup is through a settings file for now; a
settings screen inside LaunchBox and the first release are next. See
[docs/design.md](docs/design.md#milestones).

Not affiliated with or endorsed by Unbroken Software, the makers of LaunchBox.

## Requirements

- LaunchBox / BigBox 14.x on Windows (developed against 14.1.0.5, .NET 10).
- An MQTT broker (Mosquitto, the Home Assistant Mosquitto add-on, EMQX, ...),
  MQTT 5 or 3.1.1, optionally with TLS.
- Home Assistant with the MQTT integration, if you want the auto-created device.

## Home Assistant entities

| Entity | Type | State |
| --- | --- | --- |
| Now playing | sensor | Game title, or `Idle`. Attributes: platform, emulator, developer, publisher, genres, release year, play count, LaunchBox game id, start time |
| Playing | binary_sensor (`running`) | On while a game runs |
| Platform | sensor | Platform of the running game |
| BigBox running | binary_sensor (`running`) | On while BigBox is open |
| Attract mode | binary_sensor | On while BigBox is in attract mode |
| BigBox locked | binary_sensor | On while BigBox is locked |
| Last played | sensor | Title of the last completed session, with its details as attributes |
| Last played at | sensor (`timestamp`) | When that session ended |
| Last session length | sensor (`duration`, s) | How long it lasted |

The device shows as unavailable when LaunchBox exits or the PC loses power
(MQTT Last Will).

## Topics

With the defaults (base topic `launchbox`, discovery prefix `homeassistant`)
and a PC named `COCKTAIL` (node id `cocktail`):

| Topic | Retained | Payload |
| --- | --- | --- |
| `launchbox/cocktail/state` | yes | JSON state document, below |
| `launchbox/cocktail/availability` | yes | `online` / `offline` |
| `homeassistant/<component>/launchbox_cocktail/<object>/config` | yes | Discovery configs |

The node id is the PC name, lower-cased, with anything outside `a-z 0-9 _ -`
replaced by `_`.

State document (field names are a stable contract):

```json
{
  "activity": "playing",
  "playing": true,
  "now_playing": "Galaga",
  "platform": "Arcade",
  "game": {
    "id": "6f1c2a4e-...",
    "title": "Galaga",
    "platform": "Arcade",
    "emulator": "MAME",
    "developer": "Namco",
    "publisher": "Midway",
    "genres": ["Shooter"],
    "release_year": 1981,
    "play_count": 12,
    "started_at": "2026-11-20T20:00:00Z"
  },
  "bigbox_running": true,
  "attract_mode": false,
  "locked": false,
  "last_played": "Pac-Man",
  "last_played_at": "2026-11-20T19:41:10Z",
  "last_session_seconds": 754,
  "last_session": { "id": "...", "title": "Pac-Man", "platform": "Arcade",
                    "started_at": "...", "ended_at": "...", "session_seconds": 754 },
  "updated_at": "2026-11-20T20:00:00Z"
}
```

`activity` is `idle`, `launching` or `playing`. A launch that LaunchBox never
confirms (it does not report failed launches) returns to `idle` after two
minutes.

## Install

Not released yet. Releases will be a zip with a SHA256 checksum on the
[GitHub Releases](https://github.com/dewab74/launchbox-mqtt/releases) page.
Until then, build the zip yourself (see "Development setup"), then:

1. Close LaunchBox and BigBox.
2. Unpack the zip so the files land in `LaunchBox\Plugins\LaunchBoxMqtt\`.
3. Start LaunchBox or BigBox once. The plugin writes a starter settings file
   (below) and does nothing else until you fill it in.

## Configuration

Settings live in
`LaunchBox\Local\Plugins\.data\LaunchBoxMqtt\files\launchbox-mqtt.json`.
Edit it, set `"enabled": true`, and restart LaunchBox/BigBox.

```json
{
  "enabled": true,
  "host": "homeassistant.local",
  "port": 8883,
  "tls": true,
  "caCertificatePath": "C:\\LaunchBox\\Local\\Plugins\\.data\\LaunchBoxMqtt\\files\\ca.pem",
  "username": "launchbox",
  "password": "<your MQTT password>",
  "nodeName": "COCKTAIL",
  "baseTopic": "launchbox",
  "discoveryPrefix": "homeassistant",
  "discoveryEnabled": true
}
```

| Key | Default | Notes |
| --- | --- | --- |
| `enabled` | `false` | Nothing connects until this is `true` |
| `host` | required | Broker host name |
| `port` | 1883, or 8883 with TLS | |
| `tls` | `false` | |
| `caCertificatePath` | OS trust store | PEM file for brokers with a private CA (may hold several certificates). The host name is still checked |
| `username` / `password` | none | See below |
| `nodeName` | the PC name | Shown in Home Assistant; normalised into the node id |
| `baseTopic` | `launchbox` | |
| `discoveryPrefix` | `homeassistant` | |
| `discoveryEnabled` | `true` | |

**The password does not stay in the file.** On the next start the plugin moves
it into LaunchBox's plugin secret store (protected for this Windows machine),
checks it reads back, and removes it from the file. To change it, write the new
one into the file again. Keys are case-sensitive, and an unknown key is an
error rather than being silently ignored.

## Troubleshooting

The plugin keeps its own log next to the settings file:
`LaunchBox\Local\Plugins\.data\LaunchBoxMqtt\files\launchbox-mqtt.log`
(one previous file is kept as `.1`). It records start-up, connection problems
with the reason (for example which certificate failed and why), and every
LaunchBox event the plugin receives.

- **"does not chain to the configured CA"**: `caCertificatePath` must contain the
  root (or an intermediate) that issued the broker certificate.
- **Entities never appear in Home Assistant**: check that `discoveryPrefix`
  matches Home Assistant's MQTT discovery prefix (default `homeassistant`).
- **Last played did not change after a very short game**: sessions under 10 s
  are treated as failed launches (LaunchBox reports a missing ROM as launched,
  then exited at once) and do not replace the previous session.
- LaunchBox notifications show connection problems once per outage in
  LaunchBox; BigBox has no notification system, so there it is log-only.

## Development setup

Clone, then **install the git hooks**:

```bash
pre-commit install
pre-commit run --all-files   # first run, to see where things stand
```

Git hooks live in `.git/hooks/`, which is not part of a clone, so this step is
per-machine and per-clone. Skipping it means your commits get no linting and no
credential-leak scanning.

The hooks run gitleaks, detect-secrets, markdownlint,
`dotnet format --verify-no-changes`, `dotnet build -warnaserror`, shellcheck and
shfmt. Linting
and secret scanning run at commit time rather than in CI; CI only re-runs the
same hooks as a backstop, plus build and test.

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) or newer.

The plugin project compiles against LaunchBox's own
`Unbroken.LaunchBox.Plugins.dll`, which is not on NuGet and is never committed.
Copy it from `<LaunchBox>\Core\` into `.launchbox-ref/` at the repo root
(gitignored), or set `LAUNCHBOX_DIR`. Without it the plugin project is skipped
and only Core builds. Then:

```bash
scripts/package-plugin.sh 0.1.0-dev.1   # -> artifacts/launchbox-mqtt-<version>.zip + .sha256
```

## Tests

```bash
dotnet test --solution LaunchBoxMqtt.slnx
```

Runs on Windows, macOS and Linux. Includes integration tests against an
in-process MQTT broker (retained discovery, Last Will, MQTT 3.1.1 fallback,
TLS with a private CA).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Conventional Commits, SemVer tags,
changes recorded in [CHANGELOG.md](CHANGELOG.md). Security issues:
[SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE).
