#!/usr/bin/env bash
set -euo pipefail

readonly PACKAGE_IDENTIFIER="io.github.codysimonds65.roblox-account-manager"
readonly BUNDLE_IDENTIFIER="io.github.codysimonds65.roblox-account-manager"
readonly EXPECTED_EXECUTABLE="RobloxAccountManager"

die() {
  echo "package-pkg.sh: $*" >&2
  exit 1
}

usage() {
  cat >&2 <<'EOF'
Usage:
  package-pkg.sh <app> <output-pkg> <rid> <numeric-version> <Developer-ID-Installer-identity> [Developer-ID-Application-identity]
  package-pkg.sh --unsigned <app> <output-pkg> <rid> <numeric-version>
  package-pkg.sh --verify <signed-and-stapled-pkg> [Developer-ID-Installer-identity]

The build form creates a script-free component package. Gatekeeper and staple
validation are performed by the --verify form after notarization/stapling.
The --unsigned form is an explicitly labeled temporary testing path; it never
claims Gatekeeper trust and must not be used for public releases.
EOF
  exit 64
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "required command is missing: $1"
}

for command_name in pkgbuild pkgutil codesign security plutil lipo file; do
  require_command "$command_name"
done

[[ "$(uname -s)" == "Darwin" ]] || die "PKG creation is supported only on macOS."

plist_value() {
  /usr/libexec/PlistBuddy -c "Print :$1" "$2" 2>/dev/null
}

resolve_identity_label() {
  local identity="$1"
  local line
  line="$(security find-identity -v -p basic | grep -F "$identity" | head -n 1 || true)"
  [[ -n "$line" ]] || return 1
  sed -E 's/.*"(.*)"$/\1/' <<<"$line"
}

canonical_file() {
  local input="$1"
  local parent
  parent="$(cd "$(dirname "$input")" && pwd -P)" || die "cannot resolve parent of $input"
  printf '%s/%s\n' "$parent" "$(basename "$input")"
}

validate_script_free_component() {
  local package_path="$1"
  local temp_root="$2"
  local expected_installer_label="${3:-}"
  local require_signature="${4:-true}"
  local payload_list="$temp_root/payload-files.txt"
  local expanded="$temp_root/expanded"

  if [[ "$require_signature" == true ]]; then
    pkgutil --check-signature "$package_path" | tee "$temp_root/signature.txt"
    grep -q 'Status: signed' "$temp_root/signature.txt" || die "PKG is not signed."
    grep -q 'Developer ID Installer' "$temp_root/signature.txt" || die "PKG is not signed by a Developer ID Installer identity."
    if [[ -n "$expected_installer_label" ]]; then
      grep -Fq "$expected_installer_label" "$temp_root/signature.txt" || die "PKG signer does not match the requested Developer ID Installer identity."
    fi
  fi

  pkgutil --payload-files "$package_path" > "$payload_list"
  if ! awk '
    BEGIN {
      prefix1 = "Applications/Roblox Account Manager.app/"
      prefix2 = "Roblox Account Manager.app/"
    }
    $0 == "" { next }
    $0 ~ /^\// || $0 ~ /(^|\/)\.\.($|\/)/ { exit 1 }
    $0 == "Applications" || $0 == "Applications/" ||
      $0 == "Roblox Account Manager.app" || $0 == "Roblox Account Manager.app/" { next }
    index($0, prefix1) == 1 || index($0, prefix2) == 1 { next }
    { exit 1 }
  ' "$payload_list"; then
    echo "Rejected PKG payload entries:" >&2
    sed -n '1,80p' "$payload_list" >&2
    die "PKG payload contains an absolute, escaping, or unexpected path."
  fi
  if grep -Eiq '(^|/)(Scripts|[^/]*\.sh)(/|$)' "$payload_list"; then
    die "PKG payload unexpectedly contains installer scripts."
  fi

  [[ ! -e "$expanded" ]] || die "PKG expansion destination already exists."
  pkgutil --expand "$package_path" "$expanded"
  [[ ! -d "$expanded/Scripts" ]] || die "PKG contains a Scripts directory; component packages must be script-free."
  [[ -f "$expanded/PackageInfo" ]] || die "expanded PKG is missing PackageInfo."
  grep -Eq "<pkg-info[^>]*identifier=\"$PACKAGE_IDENTIFIER\"" "$expanded/PackageInfo" || \
    die "PKG identifier is not stable."
}

verify_package() {
  [[ $# == 2 || $# == 3 ]] || usage
  local package_path
  package_path="$(canonical_file "$2")"
  [[ -f "$package_path" && ! -L "$package_path" ]] || die "PKG does not exist or is a symlink: $package_path"
  local temp_root
  temp_root="$(mktemp -d "${TMPDIR:-/tmp}/ram-pkg-verify.XXXXXX")"
  trap 'rm -rf -- "$temp_root"' EXIT

  expected_installer_label=""
  if [[ $# == 3 ]]; then
    expected_installer_label="$(resolve_identity_label "$3")" || die "identity is not installed: $3"
    [[ "$expected_installer_label" == *"Developer ID Installer"* ]] || die "identity is not a Developer ID Installer certificate: $3"
  fi
  validate_script_free_component "$package_path" "$temp_root" "$expected_installer_label"
  spctl --assess --type install --verbose=2 "$package_path"
  require_command xcrun
  xcrun stapler validate "$package_path"
  echo "Verified signed, script-free, stapled PKG: $package_path"
}

[[ $# -ge 1 ]] || usage
if [[ "$1" == "--verify" ]]; then
  verify_package "$@"
  exit 0
fi

unsigned=false
if [[ "$1" == "--unsigned" ]]; then
  [[ $# == 5 ]] || usage
  unsigned=true
  app_path="$(canonical_file "$2")"
  # The workflow stages into a fresh dist directory. Create only the explicit
  # output parent before canonicalizing it; never resolve or remove a broad path.
  mkdir -p -- "$(dirname "$3")"
  output_path="$(canonical_file "$3")"
  rid="$4"
  version="$5"
  installer_identity=""
  application_identity=""
else
  [[ $# == 5 || $# == 6 ]] || usage
  app_path="$(canonical_file "$1")"
  # The workflow stages into a fresh dist directory. Create only the explicit
  # output parent before canonicalizing it; never resolve or remove a broad path.
  mkdir -p -- "$(dirname "$2")"
  output_path="$(canonical_file "$2")"
  rid="$3"
  version="$4"
  installer_identity="$5"
  application_identity="${6:-${EXPECTED_APPLICATION_IDENTITY:-}}"
fi

[[ -d "$app_path" && "$app_path" == *.app && ! -L "$app_path" ]] || die "input must be a real .app directory: $app_path"
[[ "$output_path" == *.pkg ]] || die "output must end in .pkg: $output_path"
[[ "$output_path" != "$app_path" ]] || die "output PKG cannot overwrite the app bundle."
[[ "$rid" == "osx-arm64" || "$rid" == "osx-x64" ]] || die "RID must be osx-arm64 or osx-x64."
[[ "$version" =~ ^[1-9][0-9]*$ ]] || die "PKG version must be a positive monotonic integer."
if [[ "$unsigned" == false ]]; then
  [[ "$installer_identity" != *$'\n'* && -n "$installer_identity" ]] || die "installer identity is empty or contains a newline."
fi

# Stage the app once and make that exact copy read-only. All subsequent plist,
# architecture, signature, and payload checks—and pkgbuild itself—operate on
# this immutable staging copy, avoiding a validation/build TOCTOU window.
temp_root="$(mktemp -d "${TMPDIR:-/tmp}/ram-pkg-build.XXXXXX")"
cleanup_build_root() {
  if [[ -n "${temp_root:-}" && -d "$temp_root" ]]; then
    chmod -R u+w "$temp_root" 2>/dev/null || true
    rm -rf "$temp_root"
  fi
}
trap cleanup_build_root EXIT
if find "$app_path/Contents" -type l -print -quit | grep -q .; then
  die "app contains a symlink; refusing a bundle path escape."
fi
staged_app="$temp_root/Roblox Account Manager.app"
ditto -- "$app_path" "$staged_app"
chmod -R u-w "$staged_app"
app_path="$staged_app"

plist="$app_path/Contents/Info.plist"
executable="$app_path/Contents/MacOS/$EXPECTED_EXECUTABLE"
[[ -f "$plist" && ! -L "$plist" ]] || die "app is missing a real Contents/Info.plist."
[[ -x "$executable" && ! -L "$executable" ]] || die "app is missing an executable $EXPECTED_EXECUTABLE."
plutil -lint "$plist" >/dev/null || die "app Info.plist is invalid."

bundle_id="$(plist_value CFBundleIdentifier "$plist" || true)"
bundle_type="$(plist_value CFBundlePackageType "$plist" || true)"
bundle_executable="$(plist_value CFBundleExecutable "$plist" || true)"
short_version="$(plist_value CFBundleShortVersionString "$plist" || true)"
bundle_version="$(plist_value CFBundleVersion "$plist" || true)"
[[ "$bundle_id" == "$BUNDLE_IDENTIFIER" ]] || die "unexpected CFBundleIdentifier: ${bundle_id:-missing}"
[[ "$bundle_type" == "APPL" ]] || die "CFBundlePackageType must be APPL."
[[ "$bundle_executable" == "$EXPECTED_EXECUTABLE" ]] || die "unexpected CFBundleExecutable: ${bundle_executable:-missing}"
[[ "$short_version" =~ ^[0-9]+(\.[0-9]+){1,2}$ ]] || die "CFBundleShortVersionString is not numeric: ${short_version:-missing}"
[[ "$bundle_version" == "$version" ]] || die "CFBundleVersion $bundle_version does not match monotonic PKG version $version."

arch="arm64"
[[ "$rid" == "osx-x64" ]] && arch="x86_64"
file_output="$(file -b "$executable")"
[[ "$file_output" == *"Mach-O"* ]] || die "app executable is not a Mach-O binary."
architectures="$(lipo -archs "$executable")"
[[ " $architectures " == *" $arch "* ]] || die "app executable architecture '$architectures' does not include $arch for $rid."

while IFS= read -r -d '' binary; do
  binary_description="$(file -b "$binary")"
  [[ "$binary_description" == *"Mach-O"* ]] || continue
  binary_architectures="$(lipo -archs "$binary")"
  [[ " $binary_architectures " == *" $arch "* ]] || die "Mach-O file has no $arch slice: $binary"
done < <(find "$app_path/Contents" -type f -print0)

installer_identity_label=""
if [[ "$unsigned" == false ]]; then
  codesign --verify --deep --strict --verbose=2 "$app_path"
  app_signature="$(codesign -dv --verbose=4 "$app_path" 2>&1 || true)"
  grep -q 'Authority=Developer ID Application' <<<"$app_signature" || die "app is not signed by a Developer ID Application identity."

  installer_identity_line="$(security find-identity -v -p basic | grep -F "$installer_identity" | head -n 1 || true)"
  [[ "$installer_identity_line" == *"Developer ID Installer"* ]] || die "identity is not an installed Developer ID Installer certificate: $installer_identity"
  installer_identity_label="$(resolve_identity_label "$installer_identity")"

  if [[ -n "$application_identity" ]]; then
    application_identity_label="$(resolve_identity_label "$application_identity")" || die "application identity is not installed: $application_identity"
    [[ "$application_identity_label" == *"Developer ID Application"* ]] || die "identity is not a Developer ID Application certificate: $application_identity"
    grep -Fq "$application_identity_label" <<<"$app_signature" || die "app signer does not match the requested Developer ID Application identity."
  fi
fi

output_parent="$(dirname "$output_path")"
mkdir -p "$output_parent"
if [[ -L "$output_path" ]]; then
  die "refusing to replace a symlink output path: $output_path"
fi
rm -f -- "$output_path"
pkgbuild_args=(
  --component "$app_path"
  --install-location /Applications
  --identifier "$PACKAGE_IDENTIFIER"
  --version "$version"
)
if [[ "$unsigned" == false ]]; then
  pkgbuild_args+=(--sign "$installer_identity")
fi
pkgbuild_args+=("$output_path")
pkgbuild "${pkgbuild_args[@]}"

if [[ "$unsigned" == true ]]; then
  validate_script_free_component "$output_path" "$temp_root" "" false
  echo "Created UNSIGNED test-only component PKG: $output_path"
else
  validate_script_free_component "$output_path" "$temp_root" "$installer_identity_label" true
  echo "Created signed, script-free component PKG: $output_path"
fi
