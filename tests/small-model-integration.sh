#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
source_dir=$(pwd)
endpoint=${1:-http://127.0.0.1:8001/v1}
model=${2:-Qwen2.5-0.5B-Instruct-4bit}
work=$(mktemp -d "${TMPDIR:-/tmp}/small-agent-test.XXXXXXXX")
trap 'rm -rf "$work"' EXIT HUP INT TERM

dotnet new install "$source_dir" --debug:custom-hive "$work/hive" >/dev/null
dotnet new local-agent-poc --debug:custom-hive "$work/hive" -o "$work/generated" \
    --endpoint "$endpoint" --smallModel "$model" >/dev/null
for source in "$work"/generated/examples/0[4-7]-small-*.cs; do
    output=$(printf 'Cats sleep on the sofa. Dogs play outside.\n' | dotnet "$source")
    if [ -z "$output" ]; then
        echo "Empty response from $(basename "$source")" >&2; exit 1
    fi
    echo "$(basename "$source"): response received"
done
echo 'Small-model integration checks passed (response correctness is not guaranteed).'
