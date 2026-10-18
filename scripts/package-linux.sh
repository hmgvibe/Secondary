#!/usr/bin/env bash
# Builds the self-contained linux-x64 AppImage, a .deb and a tar.gz of the same files, then starts the
# AppImage candidate once with an isolated profile. Usage: scripts/package-linux.sh [--version X.Y.Z] [--output DIR] [--offline]
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$(sed -n 's:.*<Version Condition="[^"]*">\([0-9.]*\)</Version>.*:\1:p' "$root/Directory.Build.props")"
output="artifacts/linux/release"
offline=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift ;;
    --output) output="$2"; shift ;;
    --offline) offline=1 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Version must be X.Y.Z" >&2; exit 2; }
output="$(realpath -m "$root/$output")"
[[ "$output" == "$root/"* ]] || { echo "Package output must be inside the project." >&2; exit 2; }
sdk="$root/.tools/dotnet/dotnet"
[[ -x "$sdk" ]] || sdk="dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1
tools="$("$root/scripts/fetch-appimage-tools.sh" | tr '\n' ' ')"
read -r appimagetool runtime <<< "$tools"
name="Secondary-v$version-linux-x86_64"
staging="$output/.staging-$$"
appdir="$staging/Secondary.AppDir"
trap 'rm -rf "$staging"' EXIT
mkdir -p "$appdir/usr/bin" "$staging/tmp"
audit=(-p:NuGetAudit=true -p:NuGetAuditMode=all)
[[ $offline == 1 ]] && { audit=(-p:NuGetAudit=false); echo "WARNING: offline package: dependency vulnerability auditing is NOT completed." >&2; }
"$sdk" publish "$root/src/Secondary.Linux/Secondary.Linux.csproj" -c Release -r linux-x64 --self-contained true -o "$appdir/usr/bin" \
  -p:Version="$version" -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:RestoreLockedMode=true -m:1 "${audit[@]}"
[[ -x "$appdir/usr/bin/secondary" ]] || { echo "Missing package component: usr/bin/secondary" >&2; exit 1; }
id=io.github.hmgvibe.Secondary
cp "$root/src/Secondary.Linux/Assets/secondary.png" "$appdir/$id.png"
cat > "$appdir/$id.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Secondary
Comment=副屏首頁、快捷導航與遊戲模式
Exec=secondary
Icon=$id
Categories=Utility;
StartupWMClass=secondary
Actions=ToggleGame;

[Desktop Action ToggleGame]
Name=進入／退出遊戲模式
Exec=secondary --toggle-game
EOF
cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/bin/secondary" "$@"
EOF
chmod +x "$appdir/AppRun"
# appimagetool itself is an AppImage; extract-and-run avoids needing FUSE on build machines. The
# runtime extracts under TMPDIR and keeps it; inside staging it is removed with the rest.
TMPDIR="$staging/tmp" APPIMAGE_EXTRACT_AND_RUN=1 ARCH=x86_64 "$appimagetool" --no-appstream --runtime-file "$runtime" "$appdir" "$staging/$name.AppImage"
tar -C "$appdir/usr/bin" -czf "$staging/$name.tar.gz" .
# .deb: the same files under /opt/secondary, a /usr/bin launcher and a system menu entry, which also
# lets GNOME grant the global shortcut without the in-app "加入應用程式選單" step.
deb="secondary_${version}_amd64"
debroot="$staging/deb"
mkdir -p "$debroot/DEBIAN" "$debroot/opt/secondary" "$debroot/usr/bin" "$debroot/usr/share/applications" "$debroot/usr/share/icons/hicolor/256x256/apps"
cp -a "$appdir/usr/bin/." "$debroot/opt/secondary/"
printf '#!/bin/sh\nexec /opt/secondary/secondary "$@"\n' > "$debroot/usr/bin/secondary"
cp "$appdir/$id.desktop" "$debroot/usr/share/applications/"
cp "$appdir/$id.png" "$debroot/usr/share/icons/hicolor/256x256/apps/"
find "$debroot" -type d -exec chmod 755 {} +
find "$debroot" -type f -exec chmod 644 {} +
chmod 755 "$debroot/usr/bin/secondary" "$debroot/opt/secondary/secondary"
[[ -f "$debroot/opt/secondary/createdump" ]] && chmod 755 "$debroot/opt/secondary/createdump"
# Native libraries the runtime, Skia and Avalonia's X11 backend load, including those opened at run time.
cat > "$debroot/DEBIAN/control" <<CONTROL
Package: secondary
Version: $version
Architecture: amd64
Maintainer: hmgvibe <hmgvibe@users.noreply.github.com>
Installed-Size: $(du -sk --exclude=DEBIAN "$debroot" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libssl3t64 | libssl3, libicu78 | libicu76 | libicu74 | libicu72 | libicu70, libx11-6, libice6, libsm6, libxcursor1, libxext6, libxfixes3, libxi6, libxrandr2
Recommends: libgl1
Suggests: mangohud
Section: utils
Priority: optional
Homepage: https://github.com/hmgvibe/Secondary
Description: Secondary-screen home, launchpad and game mode
 Clocks, widgets, a shortcut launchpad and a game-mode performance panel for
 a second display; can connect to the Windows helper on a main computer.
CONTROL
dpkg-deb --root-owner-group -Zxz --build "$debroot" "$staging/$deb.deb" >/dev/null
dpkg-deb --contents "$staging/$deb.deb" | grep '\./opt/secondary/secondary$' >/dev/null || { echo "Missing package component in .deb: /opt/secondary/secondary" >&2; exit 1; }
# Start the candidate once; it renders, writes a receipt and exits on its own.
receipt="$staging/startup-check.json"
TMPDIR="$staging/tmp" APPIMAGE_EXTRACT_AND_RUN=1 timeout 60 "$staging/$name.AppImage" --startup-check="$receipt"
grep -Eq '"Status": *"Started"' "$receipt" || { echo "Candidate did not start and close cleanly." >&2; exit 1; }
# The .deb payload starts the same way from its unpacked /opt/secondary.
dpkg-deb -x "$staging/$deb.deb" "$staging/deb-check"
timeout 60 "$staging/deb-check/opt/secondary/secondary" --startup-check="$staging/deb-check.json"
grep -Eq '"Status": *"Started"' "$staging/deb-check.json" || { echo ".deb candidate did not start and close cleanly." >&2; exit 1; }
mkdir -p "$output"
mv -f "$staging/$name.AppImage" "$staging/$name.tar.gz" "$staging/$deb.deb" "$output/"
(cd "$output" && sha256sum "$name.AppImage" "$deb.deb" "$name.tar.gz" > "$name.SHA256SUMS.txt")
echo "$output/$name.AppImage"
echo "$output/$deb.deb"
