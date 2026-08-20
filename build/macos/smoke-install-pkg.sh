#!/usr/bin/env bash
set -euo pipefail

readonly PACKAGE_IDENTIFIER="io.github.codysimonds65.roblox-account-manager"
readonly APP_RELATIVE_PATH="Applications/Roblox Account Manager.app"
readonly APP_NAME="Roblox Account Manager.app"
readonly EXECUTABLE_RELATIVE_PATH="Contents/MacOS/RobloxAccountManager"

die() {
  echo "smoke-install-pkg.sh: $*" >&2
  exit 1
}

usage() {
  cat >&2 <<'EOF'
Usage:
  smoke-install-pkg.sh <initial-pkg> <newer-pkg> [package-identifier]

Installs both packages into a disposable APFS image, verifies the installed
application and receipt, removes only the installed application bundle while
retaining the receipt, and reinstalls the newer package.
EOF
  exit 64
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "required command is missing: $1"
}

canonical_file() {
  local input="$1"
  local parent
  [[ "$input" == /* ]] || input="$PWD/$input"
  parent="$(cd -- "$(dirname -- "$input")" && pwd -P)" || die "cannot resolve package path: $input"
  printf '%s/%s\n' "$parent" "$(basename -- "$input")"
}

[[ $# == 2 || $# == 3 ]] || usage
for command_name in hdiutil installer pkgutil mktemp; do
  require_command "$command_name"
done

initial_pkg="$(canonical_file "$1")"
newer_pkg="$(canonical_file "$2")"
package_identifier="${3:-$PACKAGE_IDENTIFIER}"

for package_path in "$initial_pkg" "$newer_pkg"; do
  [[ -f "$package_path" && ! -L "$package_path" ]] || die "PKG does not exist or is a symlink: $package_path"
done
[[ "$package_identifier" =~ ^[A-Za-z0-9][A-Za-z0-9.+_-]*$ ]] || die "package identifier is not a safe package identifier."

work_root="$(mktemp -d "${TMPDIR:-/tmp}/ram-pkg-smoke.XXXXXX")"
image="$work_root/install.dmg"
mountpoint="$work_root/volume"
app_path="$mountpoint/$APP_RELATIVE_PATH"
executable_path="$app_path/$EXECUTABLE_RELATIVE_PATH"

cleanup() {
  set +e
  if [[ "${mounted:-false}" == true ]]; then
    if ! /usr/bin/hdiutil detach -quiet "$mountpoint" >/dev/null 2>&1; then
      echo "smoke-install-pkg.sh: refusing to remove the work directory while the APFS volume is mounted: $mountpoint" >&2
      return 1
    fi
    mounted=false
  fi
  /bin/chmod -R u+w "$work_root" >/dev/null 2>&1 || true
  /bin/rm -rf -- "$work_root"
}
trap cleanup EXIT
mounted=false

detect_layout() {
  local package_path="$1"
  local label="$2"
  local payload_file="$work_root/payload-$label.txt"
  local path
  local normalized
  local saw_root=false
  local saw_component=false

  /usr/sbin/pkgutil --payload-files "$package_path" > "$payload_file" ||
    die "could not inspect payload paths: $package_path"

  while IFS= read -r path; do
    [[ -n "$path" ]] || continue
    normalized="${path#./}"
    [[ -z "$normalized" || "$normalized" == "." ]] && continue
    [[ "$normalized" != /* ]] || die "PKG contains an absolute payload path: $path"
    [[ "$normalized" != ".." && "$normalized" != ../* && "$normalized" != */../* ]] ||
      die "PKG contains a path traversal entry: $path"

    case "$normalized" in
      Applications|Applications/|"$APP_RELATIVE_PATH"|"$APP_RELATIVE_PATH"/*)
        saw_root=true
        ;;
      "$APP_NAME"|"$APP_NAME"/*)
        saw_component=true
        ;;
      *)
        die "PKG contains an unexpected payload path: $path"
        ;;
    esac
  done < "$payload_file"

  if [[ "$saw_root" == true && "$saw_component" == true ]]; then
    die "PKG has both root and component application payload paths: $package_path"
  elif [[ "$saw_root" == true ]]; then
    echo "root"
  elif [[ "$saw_component" == true ]]; then
    echo "component"
  else
    die "PKG payload does not contain the expected application bundle: $package_path"
  fi
}

inspect_package_metadata() {
  local package_path="$1"
  local label="$2"
  local layout="$3"
  local expansion="$work_root/metadata-$label"
  local package_infos
  local package_info_count
  local package_info
  local identifier
  local package_version
  local install_location

  /bin/mkdir -p -- "$expansion"
  /usr/sbin/pkgutil --expand-full "$package_path" "$expansion/expanded" ||
    die "could not expand PKG metadata: $package_path"
  [[ ! -d "$expansion/expanded/Scripts" ]] || die "PKG contains installer scripts: $package_path"

  package_infos="$(/usr/bin/find "$expansion/expanded" -type f -name PackageInfo -print)"
  package_info_count="$(printf '%s\n' "$package_infos" | /usr/bin/sed '/^$/d' | /usr/bin/wc -l | /usr/bin/tr -d ' ')"
  [[ "$package_info_count" == 1 ]] || die "PKG must contain exactly one PackageInfo: $package_path"
  package_info="$package_infos"

  package_info_header="$(/usr/bin/tr '\n' ' ' < "$package_info")"
  package_attribute() {
    local attribute="$1"
    local value
    value="$(/usr/bin/sed -nE "s/.*[[:space:]]${attribute}[[:space:]]*=[[:space:]]*\"([^\"]+)\".*/\1/p" "$package_info" | /usr/bin/head -n 1)"
    if [[ -z "$value" ]]; then
      value="$(/usr/bin/sed -nE "s/.*[[:space:]]${attribute}[[:space:]]*=[[:space:]]*'([^']+)'.*/\1/p" "$package_info" | /usr/bin/head -n 1)"
    fi
    if [[ -z "$value" ]]; then
      value="$(printf '%s\n' "$package_info_header" | /usr/bin/sed -nE "s/.*[[:space:]]${attribute}[[:space:]]*=[[:space:]]*\"([^\"]+)\".*/\1/p")"
    fi
    if [[ -z "$value" ]]; then
      value="$(printf '%s\n' "$package_info_header" | /usr/bin/sed -nE "s/.*[[:space:]]${attribute}[[:space:]]*=[[:space:]]*'([^']+)'.*/\1/p")"
    fi
    printf '%s\n' "$value"
  }
  identifier="$(package_attribute identifier)"
  package_version="$(package_attribute version)"
  install_location="$(package_attribute install-location)"
  [[ "$identifier" == "$PACKAGE_IDENTIFIER" ]] || die "PKG identifier mismatch: $package_path"
  [[ "$package_version" =~ ^[0-9]+(\.[0-9]+)*$ ]] || die "PKG version is not numeric: ${package_version:-<missing>} ($package_path)"

  case "$layout:$install_location" in
    root:/|component:/Applications) ;;
    *) die "PKG payload layout does not match PackageInfo install-location: $package_path" ;;
  esac

  printf '%s\n' "$package_version"
}

assert_application() {
  local phase="$1"
  [[ -d "$app_path" && ! -L "$app_path" ]] ||
    die "$phase: missing application bundle: $app_path"
  [[ -x "$executable_path" && ! -L "$executable_path" ]] ||
    die "$phase: missing executable: $executable_path"
}

receipt_status() {
  local target="$1"
  local expected_version="$2"
  local info
  local receipt_dir
  local receipt_file
  local receipt_id
  local receipt_version

  # --volume is the supported way to query receipts belonging to an
  # alternate mounted target volume. Keep the alternate spelling for older
  # pkgutil builds whose option parser is order-sensitive.
  if info="$(/usr/sbin/pkgutil --pkg-info --volume "$target" "$package_identifier" 2>/dev/null)" &&
     printf '%s\n' "$info" | /usr/bin/grep -Fq "package-id: $package_identifier" &&
     printf '%s\n' "$info" | /usr/bin/grep -Fq "version: $expected_version"; then
    return 0
  fi
  if info="$(/usr/sbin/pkgutil --volume "$target" --pkg-info "$package_identifier" 2>/dev/null)" &&
     printf '%s\n' "$info" | /usr/bin/grep -Fq "package-id: $package_identifier" &&
     printf '%s\n' "$info" | /usr/bin/grep -Fq "version: $expected_version"; then
    return 0
  fi

  # Some macOS environments do not expose alternate-volume receipts through
  # pkgutil. A directly verified receipt store is the only fallback; otherwise
  # the smoke test must fail rather than claim success without receipt evidence.
  for receipt_dir in "$target/var/db/receipts" "$target/private/var/db/receipts"; do
    receipt_file="$receipt_dir/$package_identifier.plist"
    [[ -f "$receipt_file" ]] || continue
    [[ -x /usr/libexec/PlistBuddy ]] || return 2
    receipt_id="$(/usr/libexec/PlistBuddy -c 'Print :pkgid' "$receipt_file" 2>/dev/null || true)"
    receipt_version="$(/usr/libexec/PlistBuddy -c 'Print :pkg-version' "$receipt_file" 2>/dev/null || true)"
    [[ "$receipt_id" == "$package_identifier" && "$receipt_version" == "$expected_version" ]] && return 0
    return 1
  done
  return 2
}

assert_receipt() {
  local phase="$1"
  local expected_version="$2"
  local status
  if receipt_status "$mountpoint" "$expected_version"; then
    echo "$phase: package receipt $expected_version present on target volume ($package_identifier)."
    return 0
  else
    status=$?
  fi

  case "$status" in
    1) die "$phase: package receipt is missing from target volume ($package_identifier)" ;;
    2) die "$phase: target-volume receipt inspection is unsupported" ;;
    *) die "$phase: package receipt inspection failed with status $status" ;;
  esac
}

initial_layout="$(detect_layout "$initial_pkg" initial)"
newer_layout="$(detect_layout "$newer_pkg" newer)"
initial_version="$(inspect_package_metadata "$initial_pkg" initial "$initial_layout")"
newer_version="$(inspect_package_metadata "$newer_pkg" newer "$newer_layout")"
echo "Initial PKG payload layout: $initial_layout"
echo "Newer PKG payload layout: $newer_layout"
echo "Initial PKG version: $initial_version"
echo "Newer PKG version: $newer_version"
[[ "$initial_version" != "$newer_version" ]] || die "newer PKG version is not greater than the initial PKG version"
sorted_versions="$(printf '%s\n' "$initial_version" "$newer_version" | /usr/bin/sort -n)"
[[ "$(printf '%s\n' "$sorted_versions" | /usr/bin/tail -n 1)" == "$newer_version" ]] ||
  die "newer PKG version is not greater than the initial PKG version"

/bin/mkdir -p -- "$mountpoint"
/usr/bin/hdiutil create -quiet -size 512m -fs APFS -volname "RAM PKG Smoke" "$image"
/usr/bin/hdiutil attach -quiet -nobrowse -mountpoint "$mountpoint" "$image"
mounted=true
[[ "$mountpoint" != "/" && -d "$mountpoint" ]] || die "refusing to use an unsafe mount target"

install_package() {
  local package_path="$1"
  local label="$2"
  echo "Installing $label package: $(basename -- "$package_path")"
  sudo -n /usr/sbin/installer -allowUntrusted -pkg "$package_path" -target "$mountpoint"
}

install_package "$initial_pkg" initial
assert_application "After initial install"
assert_receipt "After initial install" "$initial_version"

# This is intentionally the only deletion from the mounted image. The exact
# path is derived from the disposable mountpoint and is revalidated before
# removal so a future edit cannot turn this into a broad filesystem delete.
[[ "$app_path" == "$mountpoint/$APP_RELATIVE_PATH" && "$mountpoint" != "/" ]] ||
  die "refusing to remove an unvalidated application path"
[[ -d "$app_path" && ! -L "$app_path" ]] || die "application bundle is not a real directory before repair"
sudo -n /bin/rm -rf -- "$app_path"
[[ ! -e "$app_path" ]] || die "receipt-only repair setup could not remove the application bundle"
assert_receipt "After deleting bundle while retaining receipt" "$initial_version"

install_package "$newer_pkg" newer
assert_application "After receipt-only repair"
assert_receipt "After receipt-only repair" "$newer_version"

echo "PKG install and receipt-only repair smoke test passed."
