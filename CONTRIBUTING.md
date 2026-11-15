# Contributing

Issues and pull requests are welcome. Please describe the problem or proposed change and include the commands and environment used to test it. For substantial changes, open an issue first to discuss the approach.

## Development

Install the .NET 11 SDK, clone the repository, and run commands from its root. NuGet packages are restored on first use. The deterministic checks do not require a model server:

```sh
sh tests/template-generation.sh
dotnet tests/chess-rules.cs
dotnet tests/chess-attribution.cs
dotnet tests/chess-local-endpoint.cs
```

Model-dependent tests require a running local oMLX server with compatible models. See the [README](README.md) for model configuration, chess integration, and the experimental macOS MXC checks. Record skipped checks and avoid treating a model response as proof of accuracy or a denial probe as proof of isolation.

## Safety

These examples are experimental and are not secure by default. Do not include secrets, private data, or untrusted input in issues, pull requests, fixtures, or logs. Avoid running experiments with sensitive files. Report security concerns privately to the repository owner where possible rather than publishing exploit details.

Contributions are accepted under the [MIT License](LICENSE).
