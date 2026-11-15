# Contributing

Issues and pull requests are welcome. Please describe the problem or proposed change and include the commands and environment used to test it. For substantial changes, open an issue first to discuss the approach.

## Development

Install the .NET 11 SDK, clone the repository, and run commands from its root. NuGet packages are restored on first use. The deterministic checks do not require a model server:

```sh
sh tests/template-generation.sh
dotnet tests/chess-rules.cs
dotnet tests/chess-attribution.cs
dotnet tests/chess-local-endpoint.cs
dotnet tests/loop-harness-deterministic.cs
dotnet examples/08-a2a-harness.cs
dotnet examples/09-mcp-harness.cs -- --self-test
dotnet build examples/10-openai-host-harness.cs
dotnet build examples/11-openrouter-harness.cs
work=$(mktemp -d)
dotnet build examples/04-memory-harness.cs -o "$work/build"
mkdir "$work/memory"
dotnet tests/harness-memory-approval-check.cs -- "$work/build/04-memory-harness.dll" "$work/memory"
rm -rf "$work"
```

With oMLX running, start `dotnet examples/10-openai-host-harness.cs` in a separate terminal and run `python3 tests/openai-host-harness-check.py`, then stop the host. Optional `dotnet examples/08-a2a-harness.cs -- --model` and `dotnet examples/09-mcp-harness.cs -- --model` exercise the local model; actual tool calls depend on the model. OpenRouter requires an explicitly provided key and has not been validated against a real account.

`python3 tests/harness-model-e2e-check.py` exercises file approval and opt-in memory against the configured local model; it requires a PTY and a running oMLX server and may fail when the model does not call the requested tools. See the [README](README.md) for model configuration, chess integration, and the experimental macOS MXC checks. Record skipped checks and avoid treating a model response as proof of accuracy or a denial probe as proof of isolation.

## Safety

These examples are experimental and are not secure by default. Do not include secrets, private data, or untrusted input in issues, pull requests, fixtures, or logs. Avoid running experiments with sensitive files. Report security concerns privately to the repository owner where possible rather than publishing exploit details.

Contributions are accepted under the [MIT License](LICENSE).
