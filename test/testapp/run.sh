#!/bin/sh
set -eu

directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
swift build --package-path "$directory" -c debug
binary=$(swift build --package-path "$directory" -c debug --show-bin-path)
app="$directory/.build/Marvin Test.app"
mkdir -p "$app/Contents/MacOS"
cp "$directory/Info.plist" "$app/Contents/Info.plist"
cp "$binary/MarvinTestApp" "$app/Contents/MacOS/MarvinTestApp"
codesign --force --sign - "$app"
if [ "${1:-}" != "--build-only" ]; then
    open "$app"
fi
