# Agent Control Plane

A proof-of-concept .NET template for experimenting with local [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) agents and [oMLX](https://github.com/jundot/omlx). Generate a project with configurable model IDs, run small file-based C# examples, or watch two agents play chess through A2A.

**Experimental, not a secure sandbox.** These examples run as ordinary local processes with your user permissions. The introductory examples perform file operations in application code; the Harness examples opt into agent tools as described below. Approvals and workspace paths are not OS isolation and do not restrict a compromised process, local model server, or malicious dependencies. Do not use with sensitive data or untrusted code. OpenShell is not integrated, and MXC is available only through an experimental opt-in launcher; see [security status](#security-status). This POC does not implement a zero-trust system.

## Quick start

Install the .NET 11 SDK and start an oMLX server on `127.0.0.1:8001`. Download the model you want beforehand and make sure the server can discover it; substitute its model ID below. NuGet restore needs network access on first run. Audio examples also require compatible speech and transcription models.

```sh
git clone https://github.com/matthewcorven/agent-control-plane.git
# A local installation of the .NET template; no globally installed MXC is needed.
dotnet new install ./agent-control-plane
dotnet new local-agent-poc -o my-agent --chatModel Qwen3.8-Flash-Next-oQ4e-mtp
cd my-agent
printf 'Say hello.\n' | dotnet examples/01-hello-world-chat-stdio.cs
```

Run `dotnet new local-agent-poc --help` for `--endpoint`, `--chatModel`, `--smallModel`, `--speechModel` and `--transcriptionModel`. Values become C# string literals, so use simple trusted values without quotes or backslashes. The configured model endpoint accepts only loopback HTTP(S); do not put credentials in endpoint URLs. The separate OpenRouter example below explicitly connects to an external service. Select the humorous alternative in `examples/common.cs` by commenting the first agent declaration and uncommenting the second. The local OpenAI-compatible client uses a non-secret dummy key; authentication is not configured.

## Examples

| Example | Explicit operation |
| --- | --- |
| `01-hello-world-chat-stdio.cs` | Prompt from stdin, response to stdout |
| `02-file-read.cs path` | Read one provided text file and summarize it |
| `03-file-write.cs new-path "prompt"` | Create a new file with the response; refuses to replace one |
| `03-file-append.cs existing-path "prompt"` | Append response to an existing file |
| `03-file-tts-by-prompt.cs output.wav "prompt"` | Generate an answer and synthesize speech |
| `03-file-tts-by-stdin.cs output.wav` | Same, with prompt on stdin |
| `03-file-audio-and-tts.cs input.wav output.wav` | Transcribe, answer, synthesize speech |
| `04-small-summarize.cs` | Summarize text from stdin with a small model |
| `05-small-rewrite.cs` | Rewrite text from stdin |
| `06-small-translate.cs` | Translate text from stdin to Spanish |
| `07-small-keywords.cs` | Extract keywords from stdin |

Run each with `dotnet examples/<name>`; for stdin use `printf 'How do I look?\n' | dotnet examples/03-file-tts-by-stdin.cs reply.wav`. Audio examples overwrite the specified output file. They send content to the local oMLX server; verify its model support and privacy configuration before use.

The small examples default to `Qwen2.5-0.5B-Instruct-4bit`, configurable with `--smallModel`; you must download it separately. The oMLX server must discover that model before use. These examples use Apple MLX; they do **not** establish CPU-only execution, and MLX does not provide a Google TPU backend. Small-model outputs are probabilistic and should be checked: the 0.5B model produced an inaccurate translation during testing. The experimental MXC launcher does not support these examples.

`sh tests/small-model-integration.sh [endpoint] [model]` generates a template and checks that all four examples return responses; it does not validate their accuracy.

## Microsoft Agent Harness examples

These file-based examples use [Microsoft Agent Harness](https://learn.microsoft.com/en-us/agent-framework/concepts/harness?pivots=programming-language-csharp) and the configured chat model. The model must support tool calling; delegation and other model-driven actions are not guaranteed. Use disposable, trusted local data. The examples disable web search and standing tool auto-approval; they are not sandboxed and are not covered by the MXC launcher.

| Example | Demonstrates |
| --- | --- |
| `01-planning-harness.cs` | Interactive planning and todos |
| `02-team-harness.cs` | A coordinator delegating meal planning to background agents |
| `03-files-harness.cs` | File tools scoped to a selected workspace, with per-operation approval |
| `04-memory-harness.cs` | Session continuity and optional reviewed persistent memory |
| `05-skills-harness.cs` | Loading instructions from a local skills directory |
| `06-loop-harness.cs` | A bounded loop with an exact-answer evaluator |
| `07-assistant-harness.cs` | Streaming assistant combining planning, delegation and approved file access |

```sh
mkdir -p ./workspace ./memory
dotnet examples/01-planning-harness.cs
dotnet examples/02-team-harness.cs -- "Plan three vegetarian dinners"
dotnet examples/03-files-harness.cs -- ./workspace
dotnet examples/04-memory-harness.cs                       # session only
dotnet examples/04-memory-harness.cs -- --memory ./memory # explicit persistence
dotnet examples/05-skills-harness.cs -- ./skills "Help me with a task"
dotnet examples/06-loop-harness.cs
dotnet examples/07-assistant-harness.cs -- ./workspace
```

Planning mode's request for confirmation is a model instruction, not an authorization boundary. The team agents run in-process; delegation depends on the model calling the background-agent tools. The loop reports an iteration limit if the cap stops it before the final response is evaluated, even if that response looks correct.

File tools display the requested operation and arguments before asking for approval; approving grants the operation, including reads and destructive changes. The files example denies approvals on redirected input. Opt-in memory files are plaintext and reads may be automatic; the example asks before mutations and denies them on redirected input. Files remain on disk, but a restarted example creates a new session and does not automatically recall the prior session's memory. Review all memory and skills content before use: skill instructions are untrusted model input, and selecting a skills directory permits instruction loading without another prompt. Skill scripts and resources are not exposed. Filesystem scoping and tool approval do not defend against malicious processes or concurrent filesystem changes. Never use secrets or sensitive data in these examples.

### Protocol integrations

The Framework protocol integrations compose with Harness agents; they are not all native Harness features. These examples are unauthenticated demonstrations, not secure services. Local processes can reach their loopback endpoints; do not run them on an untrusted host or expose them through a proxy. MCP tools execute with your user's permissions.

| Example | Demonstrates |
| --- | --- |
| `08-a2a-harness.cs` | A2A agent-card discovery and HTTP+JSON; streamed events and background task polling with a deterministic SDK handler. `--model` instead hosts a Harness agent and streams its A2A response. |
| `09-mcp-harness.cs` | A local MCP stdio server and client, with its arithmetic tool available to a Harness agent. |
| `10-openai-host-harness.cs` | A Harness agent hosted on loopback with OpenAI-compatible Chat Completions and Responses endpoints. |

```sh
dotnet examples/08-a2a-harness.cs              # deterministic streaming and background task polling
dotnet examples/08-a2a-harness.cs -- --model   # Harness over A2A; local model required
dotnet examples/09-mcp-harness.cs -- --self-test
dotnet examples/09-mcp-harness.cs -- --model   # model chooses whether to call MCP
dotnet examples/10-openai-host-harness.cs      # local model required; Ctrl+C stops the host
```

The A2A background path uses the A2A SDK's task handler and store, not an in-process Harness background agent. The Framework's hosted A2A server currently does not support background responses; `--model` demonstrates streaming but not background task execution. These examples do not implement ACP. The OpenAI-compatible host listens on `127.0.0.1:5079` without authentication, so any local process can use it. Hosting packages are prerelease and do not supply production authentication, authorization, or deployment protections. The host is distinct from using OpenRouter as an outbound model provider.

### Optional OpenRouter model provider

`dotnet examples/11-openrouter-harness.cs` explicitly connects a Harness agent to OpenRouter's HTTPS Chat Completions API. Unlike the local examples, this sends your prompt to an external service and its selected provider; their data policies and charges apply. Choose a tool-capable model ID and enter your key in the interactive terminal (hidden input); do not paste it into chat, source files, command arguments, or logs. The endpoint is fixed, proxies and redirects are disabled, and no credentials are saved by the example. Credentials necessarily remain in process memory during the request. This provider path has not yet been validated against a real OpenRouter account.

## Chess experiment

Run `dotnet examples/chess.cs -- --plies 40` in a terminal; press Escape to stop. The Coordinator is a Harness agent with two A2A background agents hosted in direct child processes. It renders the board with Terminal.Gui and validates each worker-attributed move before applying it. `--headless` prints board updates instead. The limit is 1–200 plies (default 40); model output or delegation failures stop the game rather than inventing a move.

This experiment uses `--chatModel` and `--endpoint`, not `--smallModel`. It requires a model capable of Harness tool calling. Each child listens on a dynamically assigned loopback port without authentication; these processes are **not sandboxed**, and other local processes may reach them. Do not use on an untrusted host. The experiment adjudicates checkmate and stalemate, but not repetition, move-count draws, dead positions, insufficient material or draw agreements. A forcibly terminated coordinator may not get an opportunity to clean up children.

From the generated directory, `dotnet tests/chess-rules.cs` and `dotnet tests/chess-attribution.cs` check deterministic rules and move attribution. `dotnet tests/chess-integration.cs` and `dotnet tests/chess-failure-cleanup.cs` exercise local oMLX and subprocess cleanup. Model-dependent tests can fail when the model does not correctly delegate or return a legal move.

## Security status

- MXC 1.1.0 is a NuGet SDK with native assets, not a required global CLI. The experimental opt-in launcher runs a published example under macOS Seatbelt, granting access to its explicit input and output paths; startup fails rather than falling back to an unsandboxed process. It preserves stdin and stdout. The normal `dotnet examples/...` commands still run outside containment.

  ```sh
  dotnet publish examples/01-hello-world-chat-stdio.cs -o /tmp/local-agent-poc-bin
  printf 'Say hello.\n' | dotnet examples/mxc-run.cs /tmp/local-agent-poc-bin/01-hello-world-chat-stdio
  dotnet publish examples/02-file-read.cs -o /tmp/local-agent-poc-bin
  dotnet examples/mxc-run.cs /tmp/local-agent-poc-bin/02-file-read ./notes.txt
  ```

  Publish each example first and pass its normal arguments to `mxc-run.cs`. The launcher supports the seven example names and rejects arguments containing quotes, backslashes, or control characters. Publish from the same generated template so its endpoint matches. It requires a numeric IPv4 HTTP loopback endpoint and opens its configured port. Only use trusted executables and paths: names alone do not authenticate binaries, and filesystem permissions are not a defense against concurrent host-side path changes. The port exception is not a destination-filtering proxy: access to other host addresses on that port may be possible. It is not an authentication mechanism; the model service remains responsible for its own access controls.

  `sh tests/mxc-integration.sh` runs all seven examples and tests denial of an unrelated file and port on macOS with oMLX running. `sh tests/template-generation.sh` verifies template substitutions and excludes Git metadata from generated output. These checks are not a security certification; normal commands and OpenShell remain unintegrated.
- OpenShell 0.1.8 provides a standalone macOS CLI artifact, but its gateway and sandbox have not been provisioned or tested here. The [official installation](https://docs.nvidia.com/openshell/about/installation) sets up a gateway service; review its effects before installing. Wiring an oMLX endpoint into a sandbox is unresolved. OpenShell's Windows MXC backend is not a macOS integration.
- A denied-loopback probe cannot establish broad network or filesystem isolation. Do not treat it as certification.

## Repository layout

- `examples/` — file-based agents, shared helpers, chess, and the experimental MXC launcher.
- `tests/` — template, chess, model integration, and containment probes.
- `.template.config/` — .NET template configuration and customization parameters.

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for setup, checks, and security guidance. Model-generated output and containment probes require careful review; please describe the environment and limitations of your results.

## Learn more

Explore [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/), [MXC](https://github.com/microsoft/mxc), [OpenShell](https://docs.nvidia.com/openshell/), and [corven-agents](https://github.com/matthewcorven/corven-agents) for agent tools, approvals, and safety patterns. This standalone experiment is not an implementation of the corven-agents hub.

## License

[MIT](LICENSE).
