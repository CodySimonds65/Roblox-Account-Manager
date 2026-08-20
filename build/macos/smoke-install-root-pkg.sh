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
}
trap cleanup EXIT

sudo -n /usr/sbin/installer -allowUntrusted -pkg "$package_path" -target /
[[ -d "$APP_PATH" && ! -L "$APP_PATH" ]] || die "Installer reported success but the application bundle is absent"

bash build/macos/verify-installed-pkg.sh "$package_path" / "$package_identifier"
echo "Root-target PKG install smoke test passed: $package_path"
