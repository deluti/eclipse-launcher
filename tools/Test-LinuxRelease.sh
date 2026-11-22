#!/usr/bin/env bash
set -euo pipefail
stage="${1:-artifacts/release/eclipse-launcher-linux-x64}"
test -x "$stage/Eclipse"
test -f "$stage/Eclipse.dll"
test -f "$stage/launcher-config.json"
test -f "$stage/packaging/eclipse-launcher.desktop"
case "$(file -b "$stage/Eclipse")" in
  *ELF*64-bit*x86-64*) ;;
  *) echo "Unexpected launcher binary: $(file -b "$stage/Eclipse")" >&2; exit 1 ;;
esac
data="$(mktemp -d)"
trap 'rm -rf "$data"' EXIT
XDG_DATA_HOME="$data" "$stage/Eclipse" --core-smoke-test
if command -v xvfb-run >/dev/null 2>&1; then
  XDG_DATA_HOME="$data" timeout 20s xvfb-run -a "$stage/Eclipse" --smoke-test
fi
echo "Linux release smoke test passed."
