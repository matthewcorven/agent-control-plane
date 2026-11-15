#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
work=$(mktemp -d "${TMPDIR:-/tmp}/mxc-poc-test.XXXXXXXX")
trap 'rm -rf "$work"' EXIT HUP INT TERM
mkdir "$work/bin" "$work/probe"
for source in examples/0*.cs; do
    dotnet publish "$source" -o "$work/bin" --nologo -v:q
done
run() { dotnet examples/mxc-run.cs "$work/bin/$@"; }
chat=$(printf 'Say hello briefly.\n' | run 01-hello-world-chat-stdio)
test -n "$chat"
printf 'This document is about playful cats.\n' > "$work/input.txt"
summary=$(run 02-file-read "$work/input.txt")
test -n "$summary"
run 03-file-write "$work/output.txt" 'Say hello briefly.'
test -s "$work/output.txt"
before=$(wc -c < "$work/output.txt")
run 03-file-append "$work/output.txt" 'Say goodbye briefly.'
after=$(wc -c < "$work/output.txt")
test "$after" -gt "$before"
if run 03-file-write "$work/output.txt" 'Do not replace this.' >/dev/null 2>&1; then
    echo 'Existing output was overwritten' >&2; exit 1
fi
test "$(wc -c < "$work/output.txt")" -eq "$after"
run 03-file-tts-by-prompt "$work/prompt.wav" 'Say hello briefly.'
printf 'Say hello briefly.\n' | run 03-file-tts-by-stdin "$work/stdin.wav"
run 03-file-audio-and-tts "$work/prompt.wav" "$work/audio.wav"
for audio in "$work/prompt.wav" "$work/stdin.wav" "$work/audio.wav"; do
    test "$(head -c 4 "$audio")" = RIFF
done
dotnet publish tests/mxc-denial-probe.cs -o "$work/probe" --nologo -v:q
cp "$work/probe/mxc-denial-probe" "$work/bin/01-hello-world-chat-stdio"
printf '%s\n' "$work/input.txt" | run 01-hello-world-chat-stdio
if dotnet examples/mxc-run.cs "$work/bin/does-not-exist" >/dev/null 2>&1; then
    echo 'Unknown executable was accepted' >&2; exit 1
fi
rm "$work/bin/01-hello-world-chat-stdio"
if dotnet examples/mxc-run.cs "$work/bin/01-hello-world-chat-stdio" >/dev/null 2>&1; then
    echo 'Missing supported executable was accepted' >&2; exit 1
fi
echo 'MXC integration checks passed.'
