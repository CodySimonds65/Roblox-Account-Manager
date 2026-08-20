#!/usr/bin/env bash
set -euo pipefail

readonly PACKAGE_IDENTIFIER="io.github.codysimonds65.roblox-account-manager"
readonly APP_NAME="Roblox Account Manager.app"
readonly APP_RELATIVE_PATH="Applications/$APP_NAME"
readonly EXECUTABLE_RELATIVE_PATH="Contents/MacOS/RobloxAccountManager"

die() {
  echo "verify-installed-pkg.sh: $*" >&2
  exit 1
}

usage() {
  cat >&2 <<'EOF'
Usage:
  verify-installed-pkg.sh <pkg> [target-volume] [package-identifier]

Read-only verification for a completed macOS PKG installation. The target
defaults to / and is never modified.
EOF
  exit 64
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "required command is missing: $1"
}

canonical_file() {
  local input="$1"
  [[ "$input" == /* ]] || input="$PWD/$input"
  local parent
  parent="$(cd -- "$(dirname -- "$input")" && pwd -P)" || die "cannot resolve package path: $input"
  printf '%s/%s\n' "$parent" "$(basename -- "$input")"
}

[[ $# == 1 || $# == 2 || $# == 3 ]] || usage
for command_name in pkgutil find sed grep mktemp; do
  require_command "$command_name"
done

package_path="$(canonical_file "$1")"
target="${2:-/}"
package_identifier="${3:-$PACKAGE_IDENTIFIER}"
[[ -f "$package_path" && ! -L "$package_path" ]] || die "PKG does not exist or is a symlink: $package_path"
[[ -d "$target" ]] || die "target volume does not exist: $target"
target="$(cd -- "$target" && pwd -P)" || die "cannot resolve target volume: $target"
[[ "$package_identifier" =~ ^[A-Za-z0-9][A-Za-z0-9.+_-]*$ ]] || die "package identifier is not a safe package identifier"

work_root="$(mktemp -d "${TMPDIR:-/tmp}/ram-pkg-verify.XXXXXX")"
cleanup() {
  /bin/chmod -R u+w "$work_root" >/dev/null 2>&1 || true
  /bin/rm -rf -- "$work_root"
}
trap cleanup EXIT

payload_list="$work_root/payload-files.txt"
expanded="$work_root/expanded"
/usr/sbin/pkgutil --payload-files "$package_path" > "$payload_list" || die "could not inspect PKG payload"
/usr/sbin/pkgutil --expand-full "$package_path" "$expanded" || die "could not expand PKG"

layout=""
while IFS= read -r path; do
  [[ -n "$path" ]] || continue
  normalized="${path#./}"
  [[ -z "$normalized" || "$normalized" == "." ]] && continue
  [[ "$normalized" != /* ]] || die "PKG contains an absolute payload path: $path"
  [[ "$normalized" != ".." && "$normalized" != ../* && "$normalized" != */../* ]] ||
    die "PKG contains a path traversal entry: $path"
  case "$normalized" in
    "$APP_RELATIVE_PATH"|"$APP_RELATIVE_PATH"/*)
      [[ -z "$layout" || "$layout" == root ]] || die "PKG mixes root and component payload layouts"
      layout=root
      ;;
    "$APP_NAME"|"$APP_NAME"/*)
      [[ -z "$layout" || "$layout" == component ]] || die "PKG mixes root and component payload layouts"
      layout=component
      ;;
    Applications|Applications/)
      ;;
    *)
      die "PKG contains an unexpected payload path: $path"
      ;;
  esac
done < "$payload_list"
[[ "$layout" == root || "$layout" == component ]] || die "PKG does not contain the expected application bundle"

package_infos="$(/usr/bin/find "$expanded" -type f -name PackageInfo -print)"
package_info_count="$(printf '%s\n' "$package_infos" | /usr/bin/sed '/^$/d' | /usr/bin/wc -l | /usr/bin/tr -d ' ')"
[[ "$package_info_count" == 1 ]] || die "PKG must contain exactly one PackageInfo"
package_info="$package_infos"

identifier="$(/usr/bin/grep -Eo 'identifier="[^"]+"' "$package_info" | /usr/bin/head -n 1 | /usr/bin/sed -E 's/^identifier="([^"]+)"$/\1/')"
package_version="$(/usr/bin/grep -Eo 'version="[^"]+"' "$package_info" | /usr/bin/head -n 1 | /usr/bin/sed -E 's/^version="([^"]+)"$/\1/')"
install_location="$(/usr/bin/grep -Eo 'install-location="[^"]+"' "$package_info" | /usr/bin/head -n 1 | /usr/bin/sed -E 's/^install-location="([^"]+)"$/\1/')"
[[ "$identifier" == "$package_identifier" ]] || die "PackageInfo identifier mismatch: $identifier"
[[ "$package_version" =~ ^[0-9]+$ ]] || die "PackageInfo version is not numeric: $package_version"
case "$layout:$install_location" in
  root:/|component:/Applications) ;;
  *) die "payload layout and PackageInfo install-location disagree" ;;
esac

app_path="$target/Applications/$APP_NAME"
[[ -d "$app_path" && ! -L "$app_path" ]] || die "installed application bundle is missing: $app_path"
[[ -x "$app_path/$EXECUTABLE_RELATIVE_PATH" && ! -L "$app_path/$EXECUTABLE_RELATIVE_PATH" ]] ||
  die "installed application executable is missing: $app_path/$EXECUTABLE_RELATIVE_PATH"

while IFS= read -r path; do
  [[ -n "$path" ]] || continue
  normalized="${path#./}"
  [[ -z "$normalized" || "$normalized" == "." ]] && continue
  case "$layout" in
    root) installed_path="$target/$normalized" ;;
    component) installed_path="$target/Applications/$normalized" ;;
  esac
  [[ -e "$installed_path" ]] || die "receipt payload file is missing: $installed_path"
done < "$payload_list"

receipt_verified=false
receipt_info="$(/usr/sbin/pkgutil --pkg-info --volume "$target" "$package_identifier" 2>/dev/null || true)"
if printf '%s\n' "$receipt_info" | /usr/bin/grep -Fq "package-id: $package_identifier" &&
   printf '%s\n' "$receipt_info" | /usr/bin/grep -Fq "version: $package_version"; then
  receipt_verified=true
fi
if [[ "$receipt_verified" != true ]]; then
  receipt_info="$(/usr/sbin/pkgutil --volume "$target" --pkg-info "$package_identifier" 2>/dev/null || true)"
  if printf '%s\n' "$receipt_info" | /usr/bin/grep -Fq "package-id: $package_identifier" &&
     printf '%s\n' "$receipt_info" | /usr/bin/grep -Fq "version: $package_version"; then
    receipt_verified=true
  fi
fi
if [[ "$receipt_verified" != true && -x /usr/libexec/PlistBuddy ]]; then
  for receipt_dir in "$target/var/db/receipts" "$target/private/var/db/receipts"; do
    receipt_file="$receipt_dir/$package_identifier.plist"
    [[ -f "$receipt_file" ]] || continue
    receipt_id="$(/usr/libexec/PlistBuddy -c 'Print :pkgid' "$receipt_file" 2>/dev/null || true)"
    receipt_version="$(/usr/libexec/PlistBuddy -c 'Print :pkg-version' "$receipt_file" 2>/dev/null || true)"
    if [[ "$receipt_id" == "$package_identifier" && "$receipt_version" == "$package_version" ]]; then
      receipt_verified=true
    fi
    break
  done
fi
[[ "$receipt_verified" == true ]] || die "package receipt is missing or has unexpected identity/version on target volume: $package_identifier"

echo "Verified installed PKG $package_version ($layout) at $app_path."
