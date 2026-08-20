#!/usr/bin/env bash
set -euo pipefail

readonly PACKAGE_IDENTIFIER="io.github.codysimonds65.roblox-account-manager"
readonly APP_PATH="/Applications/Roblox Account Manager.app"

die() {
  echo "smoke-install-root-pkg.sh: $*" >&2
  exit 1
}

[[ $# == 1 || $# == 2 ]] || {
  echo "Usage: smoke-install-root-pkg.sh <pkg> [package-identifier]" >&2
  exit 64
}

command -v sudo >/dev/null 2>&1 || die "sudo is required"
command -v installer >/dev/null 2>&1 || die "installer is required"
command -v pkgutil >/dev/null 2>&1 || die "pkgutil is required"

package_path="$1"
package_identifier="${2:-$PACKAGE_IDENTIFIER}"
[[ "$package_identifier" =~ ^[A-Za-z0-9][A-Za-z0-9.+_-]*$ ]] || die "package identifier is not safe"
[[ "$package_path" == /* ]] || package_path="$PWD/$package_path"
package_path="$(cd -- "$(dirname -- "$package_path")" && pwd -P)/$(basename -- "$package_path")"
[[ -f "$package_path" && ! -L "$package_path" ]] || die "PKG does not exist or is a symlink: $package_path"
source_app_path="$(dirname -- "$package_path")/Roblox Account Manager.app"
[[ -d "$source_app_path" && ! -L "$source_app_path" ]] ||
  die "source application bundle is missing beside the PKG: $source_app_path"

# A root package must install its bundle at the explicit Applications path.
# pkgbuild can otherwise emit relocation metadata that allows Installer to
# select a matching bundle outside /Applications, including the source app
# left beside a release PKG in the workflow workspace.
metadata_root="$(mktemp -d "${TMPDIR:-/tmp}/ram-root-pkg-metadata.XXXXXX")"
cleanup_metadata() {
  /bin/rm -rf -- "$metadata_root"
}
trap cleanup_metadata EXIT
/usr/sbin/pkgutil --expand "$package_path" "$metadata_root/expanded"
package_info="$metadata_root/expanded/PackageInfo"
[[ -f "$package_info" ]] || die "expanded PKG is missing PackageInfo"
if /usr/bin/grep -Eiq '<relocate([[:space:]>]|$)' "$package_info"; then
  die "root-target PKG contains relocatable bundle metadata"
fi

[[ ! -e "$APP_PATH" && ! -L "$APP_PATH" ]] || die "refusing to overwrite an existing application: $APP_PATH"

# A hosted runner can retain a receipt from an earlier validation attempt even
# after the application bundle was removed. Installer may report a successful
# upgrade while restoring no files in that state, so make the host-root test
# deterministic by forgetting only this exact package identifier first.
if receipt_info="$(sudo -n /usr/sbin/pkgutil --pkg-info "$package_identifier" 2>/dev/null)"; then
  echo "Removing stale root-volume receipt for $package_identifier."
  sudo -n /usr/sbin/pkgutil --forget "$package_identifier" >/dev/null ||
    die "could not forget the stale root-volume receipt: $package_identifier"
fi
if sudo -n /usr/sbin/pkgutil --pkg-info "$package_identifier" >/dev/null 2>&1; then
  die "root-volume receipt remained after cleanup: $package_identifier"
fi

cleanup() {
  set +e
  if [[ -e "$APP_PATH" || -L "$APP_PATH" ]]; then
    sudo -n /bin/rm -rf -- "$APP_PATH"
  fi
  sudo -n /usr/sbin/pkgutil --forget "$package_identifier" >/dev/null 2>&1 || true
  cleanup_metadata
}
trap cleanup EXIT

sudo -n /usr/sbin/installer -allowUntrusted -pkg "$package_path" -target /
[[ -d "$APP_PATH" && ! -L "$APP_PATH" ]] || die "Installer reported success but the application bundle is absent"
[[ -d "$source_app_path" && ! -L "$source_app_path" ]] ||
  die "Installer moved or removed the source application bundle: $source_app_path"

bash build/macos/verify-installed-pkg.sh "$package_path" / "$package_identifier"
[[ -d "$source_app_path" && ! -L "$source_app_path" ]] ||
  die "Package verification moved or removed the source application bundle: $source_app_path"
echo "Root-target PKG install smoke test passed: $package_path"
