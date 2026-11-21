# Security policy

## Reporting a vulnerability

Please report security problems privately through
[GitHub private vulnerability reporting](https://github.com/dewab74/launchbox-mqtt/security/advisories/new),
not in a public issue. Expect an acknowledgement within a week.

## What counts

- Anything that lets an MQTT broker user make the LaunchBox PC run a program,
  open a file, or launch something other than a game already in the LaunchBox
  library. Command topics (planned for v2, off by default) accept a LaunchBox
  game id only, never a path or command line.
- Broker credentials written anywhere other than LaunchBox's plugin secret
  store, or appearing in logs.
- TLS certificate validation that accepts a certificate it should not (wrong
  host name, untrusted chain).

## Supported versions

Only the latest release is supported.
