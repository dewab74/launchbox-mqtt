#!/usr/bin/env bash
# Build the LaunchBox plugin and package it as a release zip with a SHA256 file.
#
#   scripts/package-plugin.sh [version]
#
# Needs Unbroken.LaunchBox.Plugins.dll in .launchbox-ref/ (copy it from
# <LaunchBox>\Core) or LAUNCHBOX_DIR pointing at a LaunchBox install. The DLL is
# compiled against, never shipped: the script fails if it lands in the output.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${1:-0.0.0-dev}"
out="$root/artifacts"
stage="$out/LaunchBoxMqtt"
zip="$out/launchbox-mqtt-$version.zip"

rm -rf "$stage" "$zip" "$zip.sha256"
mkdir -p "$stage"

args=(-c Release -o "$stage" -p:RequireLaunchBoxApi=true -p:Version="$version")
if [[ -n ${LAUNCHBOX_DIR:-} ]]; then
  args+=(-p:LaunchBoxDir="$LAUNCHBOX_DIR")
fi
dotnet publish "$root/src/LaunchBoxMqtt.Plugin/LaunchBoxMqtt.Plugin.csproj" "${args[@]}"

# Validate the output rather than trusting the exit code.
for required in LaunchBoxMqtt.Plugin.dll LaunchBoxMqtt.Core.dll MQTTnet.dll; do
  [[ -f "$stage/$required" ]] || {
    echo "error: $required missing from $stage" >&2
    exit 1
  }
done
if compgen -G "$stage/Unbroken.*" >/dev/null; then
  echo "error: LaunchBox assemblies were copied into the package; they must not ship" >&2
  exit 1
fi
rm -f "$stage"/*.pdb "$stage"/*.xml

cp "$root/LICENSE" "$stage/LICENSE.txt"
(cd "$out" && zip -qr "$(basename "$zip")" LaunchBoxMqtt)
(cd "$out" && shasum -a 256 "$(basename "$zip")" >"$(basename "$zip").sha256")

echo "Packaged $zip"
cat "$zip.sha256"
