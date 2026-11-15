#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
source_dir=$(pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/agent-template-test.XXXXXXXX")
trap 'rm -rf "$work"' EXIT HUP INT TERM

dotnet new install "$source_dir" --debug:custom-hive "$work/hive" >/dev/null
dotnet new local-agent-poc --debug:custom-hive "$work/hive" -o "$work/generated" \
    --chatModel custom-chat-model --endpoint http://127.0.0.1:8765/v1 >/dev/null
if [ -e "$work/generated/.git" ] || [ -e "$work/generated/.template.config" ]; then
    echo 'Generated output contains template repository metadata' >&2; exit 1
fi
for source in examples/common.cs examples/mxc-run.cs; do
    if ! grep -Fq 'http://127.0.0.1:8765/v1' "$work/generated/$source"; then
        echo "Endpoint substitution missing in $source" >&2; exit 1
    fi
done
if ! grep -Fq 'custom-chat-model' "$work/generated/examples/common.cs"; then
    echo 'Chat model substitution missing in examples/common.cs' >&2; exit 1
fi
echo 'Template generation checks passed.'
