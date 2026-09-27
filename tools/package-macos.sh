#!/usr/bin/env bash
# macOS 用のアプリ（PwVault.app）を作って zip にする。GitHub Actions の macOS 環境で使う。
#   tools/package-macos.sh <版> <arm64|x64>
# Apple の開発者登録がないので署名は「ad-hoc」（Apple Silicon で動かすのに最低限必要なもの）。
# 公証していないため、初回起動時に Gatekeeper の警告が出る（README に開き方を記載）。
set -euo pipefail

version="$1"
arch="$2"
rid="osx-$arch"
out="out/$rid"
app="dist/$rid/PwVault.app"
zip="PwVault-$version-$rid.zip"

dotnet publish src/PwVault.App -c Release -r "$rid" --self-contained \
  -p:Version="$version" -p:DebugType=none -o "$out"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$out/." "$app/Contents/MacOS/"

# アイコン（.ico → .icns）。失敗しても既定のアイコンで続ける
tmp="$(mktemp -d)"
if sips -s format png src/PwVault.App/Assets/avalonia-logo.ico --out "$tmp/icon.png" >/dev/null 2>&1; then
  mkdir -p "$tmp/PwVault.iconset"
  for size in 16 32 128 256 512; do
    sips -z $size $size "$tmp/icon.png" --out "$tmp/PwVault.iconset/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) "$tmp/icon.png" --out "$tmp/PwVault.iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$tmp/PwVault.iconset" -o "$app/Contents/Resources/PwVault.icns" || true
fi
rm -rf "$tmp"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>PwVault</string>
  <key>CFBundleDisplayName</key><string>PwVault</string>
  <key>CFBundleIdentifier</key><string>io.github.falledcan.pwvault</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleExecutable</key><string>PwVault</string>
  <key>CFBundleIconFile</key><string>PwVault</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

chmod +x "$app/Contents/MacOS/PwVault"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

ditto -c -k --keepParent "$app" "$zip"
shasum -a 256 "$zip" > "$zip.sha256"
echo "packaged $zip"
