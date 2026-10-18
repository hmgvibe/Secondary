#!/usr/bin/env bash
# Locked restore, dependency audit, Release build and the cross-platform tests for the Linux version.
# Usage: scripts/build-linux.sh [--offline] [--output DIR]
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="artifacts/linux/verification"
offline=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --offline) offline=1 ;;
    --output) output="$2"; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done
output="$(realpath -m "$root/$output")"
[[ "$output" == "$root/"* ]] || { echo "Verification output must be inside the project." >&2; exit 2; }
sdk="$root/.tools/dotnet/dotnet"
[[ -x "$sdk" ]] || sdk="dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1
mkdir -p "$output"
receipt="$output/verification.json"
status=Failed audit=false core=false shell=false
write_receipt() {
  printf '{"Status":"%s","Platform":"linux-x64","DependencyAuditCompleted":%s,"CorePassed":%s,"LinuxShellPassed":%s,"CompletedUtc":"%s"}\n' \
    "$status" "$audit" "$core" "$shell" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$receipt"
}
trap write_receipt EXIT
cd "$root"
projects=(src/Secondary.Core/Secondary.Core.csproj src/Secondary.Shared/Secondary.Shared.csproj src/Secondary.Linux/Secondary.Linux.csproj
          tests/Secondary.Tests/Secondary.Tests.csproj tests/Secondary.Linux.Tests/Secondary.Linux.Tests.csproj)
for project in "${projects[@]}"; do
  if [[ $offline == 1 ]]; then "$sdk" restore "$project" --locked-mode --ignore-failed-sources -p:NuGetAudit=false
  else "$sdk" restore "$project" --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all; fi
done
[[ $offline == 1 ]] && echo "WARNING: offline verification: dependency vulnerability auditing is NOT completed." >&2 || audit=true
for project in src/Secondary.Linux/Secondary.Linux.csproj tests/Secondary.Tests/Secondary.Tests.csproj tests/Secondary.Linux.Tests/Secondary.Linux.Tests.csproj; do
  "$sdk" build "$project" -c Release --no-restore -m:1
done
"$sdk" run --project tests/Secondary.Tests -c Release --no-build
core=true
# Tests write their operation records into the output, never into the user's XDG state.
XDG_STATE_HOME="$output/state" "$sdk" run --project tests/Secondary.Linux.Tests -c Release --no-build -- "$output/frames"
shell=true
status=Passed
