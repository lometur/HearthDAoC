#!/usr/bin/env bash
# Build the Linux admin CLIs into OUT/<tool>/. Usage: tools/linux/build.sh OUT
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:?usage: $0 <output dir>}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
for tool in offline-bots bot-goals progress-import; do
    dotnet build "$here/$tool/$tool.csproj" -c Release -o "$out/$tool" --nologo -v quiet
done
echo "Built: $(ls "$out" | tr '\n' ' ')"
