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

**Early development, not yet released.** The platform-neutral core (state
tracking, payloads, discovery, MQTT transport) is built and tested. The
LaunchBox plugin shell and settings UI are next; see
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
[GitHub Releases](https://github.com/dewab74/launchbox-mqtt/releases) page, to
unpack into `LaunchBox\Plugins\`.

## Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| Broker host | required | |
| Port | 1883, or 8883 with TLS | |
| TLS | off | |
| CA certificate (PEM) | OS trust store | For brokers with a private CA; host name is still checked |
| Username / password | none | The password is kept in LaunchBox's plugin secret store, not a plain file |
| Base topic | `launchbox` | |
| Discovery prefix | `homeassistant` | |
| Home Assistant discovery | on | |

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
`dotnet format --verify-no-changes` and `dotnet build -warnaserror`. Linting
and secret scanning run at commit time rather than in CI; CI only re-runs the
same hooks as a backstop, plus build and test.

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) or newer.

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
