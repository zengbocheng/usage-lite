#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
out=${1:-"$PWD/artifacts/macos"}
arch=$(uname -m)
case "$arch" in
    arm64) rid=osx-arm64 ;;
    x86_64) rid=osx-x64 ;;
    *) echo "Unsupported architecture: $arch" >&2; exit 2 ;;
esac
version=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' UsageLite.csproj)
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]

app="$out/Usage Lite.app"
contents="$app/Contents"
reader="$contents/Helpers/Reader"
mkdir -p "$contents/MacOS" "$contents/Resources" "$reader"
cp macos/Info.plist "$contents/Info.plist"
for key in CFBundleShortVersionString CFBundleVersion; do
    /usr/libexec/PlistBuddy -c "Set :$key $version" "$contents/Info.plist"
done
cp LICENSE THIRD_PARTY_NOTICES.md "$contents/Resources/"
cp -R licenses "$contents/Resources/licenses"

dotnet publish macos/Reader/UsageLite.Reader.csproj -c Release -r "$rid" --self-contained true -o "$reader" -p:Version="$version"
xcrun swiftc -swift-version 5 -parse-as-library -O -target "$arch-apple-macosx14.0" \
    macos/Sources/*.swift -o "$contents/MacOS/UsageLite"

# Ad-hoc signing makes the preview bundle internally consistent on both architectures.
find "$reader" -type f -name '*.dylib' -exec codesign --force --sign - {} +
codesign --force --sign - "$reader/usage-lite-reader"
codesign --force --sign - "$app"
codesign --verify --deep --strict "$app"
ditto -c -k --keepParent "$app" "$out/Usage-Lite-$version-$rid.zip"
