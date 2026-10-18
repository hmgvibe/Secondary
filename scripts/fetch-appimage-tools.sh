#!/usr/bin/env bash
# Downloads the pinned appimagetool and AppImage runtime into .tools/appimage and verifies SHA256.
# A mismatching download is kept for inspection and never executed.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
directory="$root/.tools/appimage"
mkdir -p "$directory"
fetch() {
  local name="$1" url="$2" expected="$3" target="$directory/$1"
  if [[ -f "$target" ]]; then
    [[ "$(sha256sum "$target" | cut -d' ' -f1)" == "$expected" ]] && { echo "$target"; return; }
    echo "Existing $name does not match the verified release; it has been preserved." >&2; exit 1
  fi
  local candidate="$directory/$name.download-$$"
  curl --fail --location --silent --show-error --output "$candidate" "$url"
  if [[ "$(sha256sum "$candidate" | cut -d' ' -f1)" != "$expected" ]]; then
    echo "Download hash mismatch for $name. Candidate preserved for inspection; it will not be executed." >&2; exit 1
  fi
  mv "$candidate" "$target"
  echo "$target"
}
fetch appimagetool-1.9.1-x86_64.AppImage \
  https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage \
  ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
fetch runtime-20251108-x86_64 \
  https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64 \
  2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
chmod +x "$directory/appimagetool-1.9.1-x86_64.AppImage"
