#!/bin/bash
set -euo pipefail

usage() {
  echo "Usage: $0 <publish-directory> <output-app> [version]" >&2
  exit 64
}

[[ $# -ge 2 && $# -le 3 ]] || usage

publish_dir="$(cd "$1" && pwd -P)"
output_app="$2"
version="${3:-0.0.0}"

[[ -f "$publish_dir/RobloxAccountManager" ]] || {
  echo "Publish directory does not contain RobloxAccountManager." >&2
  exit 1
}

[[ "$output_app" == *.app ]] || {
  echo "Output path must end in .app." >&2
  exit 64
}

script_dir="$(cd "$(dirname "$0")" && pwd -P)"
plist_template="$script_dir/Info.plist"
[[ -f "$plist_template" ]] || {
  echo "Missing Info.plist template: $plist_template" >&2
  exit 1
}

rm -rf -- "$output_app"
mkdir -p "$output_app/Contents/MacOS" "$output_app/Contents/Resources"

# ditto preserves the native files and bundle layout better than a shell glob.
ditto "$publish_dir/" "$output_app/Contents/MacOS/"
cp "$plist_template" "$output_app/Contents/Info.plist"

/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" \
  "$output_app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $version" \
  "$output_app/Contents/Info.plist"

# The managed app executable and native libraries must be executable after
# extraction. Signing/notarization is intentionally performed by the release
# workflow after this step.
find "$output_app/Contents/MacOS" -type f \( -name '*.dylib' -o -name 'RobloxAccountManager' -o -name 'createdump' \) -exec chmod u+x {} +

if [[ -n "${RAM_TRUSTED_INSTALLER_IDENTITY:-}" ]]; then
  printf '%s\n' "$RAM_TRUSTED_INSTALLER_IDENTITY" > \
    "$output_app/Contents/Resources/RobloxInstallerIdentity"
  chmod 600 "$output_app/Contents/Resources/RobloxInstallerIdentity"
fi

echo "Created $output_app"
