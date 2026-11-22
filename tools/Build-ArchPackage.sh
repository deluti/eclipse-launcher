#!/usr/bin/env bash
set -euo pipefail

version="${1:-0.6.0}"
case "$version" in
  *[!0-9.]*|'') echo 'Version must be MAJOR.MINOR.PATCH.' >&2; exit 2 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
release="$root/artifacts/release"
archive="$release/eclipse-launcher-linux-x64.tar.gz"
work="$root/.test-data/arch-package"
output="$release/eclipse-launcher-linux-x64.pkg.tar.zst"
test -f "$archive"

rm -rf "$work"
mkdir -p "$work"
cp "$archive" "$work/"
sha="$(sha256sum "$archive" | awk '{print $1}')"
sed -e "s/@VERSION@/$version/g" -e "s/@SHA256@/$sha/g" "$root/packaging/arch/PKGBUILD.in" > "$work/PKGBUILD"

docker run --rm -v "$work:/build" archlinux:base-devel bash -lc \
  'useradd --create-home builder && chown -R builder:builder /build && runuser -u builder -- bash -lc "cd /build && makepkg --nodeps --noconfirm"'

package="$(find "$work" -maxdepth 1 -type f -name 'eclipse-launcher-bin-*.pkg.tar.zst' -print -quit)"
test -n "$package"
cp "$package" "$output"

tar_hash="$(sha256sum "$archive" | awk '{print $1}')"
pkg_hash="$(sha256sum "$output" | awk '{print $1}')"
printf '%s  %s\n%s  %s\n' "$tar_hash" "$(basename "$archive")" "$pkg_hash" "$(basename "$output")" > "$release/SHA256SUMS-linux.txt"
echo "Ready: $output"
