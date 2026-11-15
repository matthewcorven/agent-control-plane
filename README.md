# Local agent template — proof of concept

**Experimental, not a secure sandbox.** These examples run as ordinary local processes with your user permissions. The agent has no registered tools; file operations are performed by the example code on paths you explicitly provide. This does not restrict a compromised process, local model server, or malicious dependencies. Do not use with sensitive data or untrusted code. OpenShell is not integrated, and MXC is available only through an experimental opt-in chat launcher; see [security status](#security-status). This POC does not implement a zero-trust system.

## Start

Requires .NET 11 SDK and an oMLX server listening on `127.0.0.1:8001`. Model IDs below must be installed in that server; pick equivalent compatible models if not. NuGet restore needs network access on first run.

```sh
git clone https://github.com/matthewcorven/agent-template-poc.git
# A local installation of the .NET template; no globally installed MXC is needed.
dotnet new install ./agent-template-poc
dotnet new local-agent-poc -o my-agent --chatModel Qwen3.8-Flash-Next-oQ4e-mtp
cd my-agent
printf 'Say hello.\n' | dotnet examples/01-hello-world-chat-stdio.cs
```

Run `dotnet new local-agent-poc --help` for `--endpoint`, `--chatModel`, `--speechModel` and `--transcriptionModel`. Values become C# string literals, so use simple trusted values without quotes or backslashes. Only loopback HTTP(S) endpoints are accepted; do not put credentials in endpoint URLs. Select the humorous alternative in `examples/common.cs` by commenting the first agent declaration and uncommenting the second. The local OpenAI-compatible client uses a non-secret dummy key; authentication is not configured.

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

Run each with `dotnet examples/<name>`; for stdin use `printf 'How do I look?\n' | dotnet examples/03-file-tts-by-stdin.cs reply.wav`. Audio examples overwrite the specified output file. They send content to the local oMLX server; verify its model support and privacy configuration before use.

## Security status

- MXC 1.1.0 is a NuGet SDK with native assets, not a required global CLI. On macOS, the experimental opt-in launcher below runs the published chat executable under Seatbelt; startup fails rather than falling back to an unsandboxed process. It preserves stdin and stdout. The normal `dotnet examples/...` commands still run outside containment. A separate test process was denied access to an unrelated file and port; this is not a security certification. MXC's port exception is not a destination-filtering proxy: access to other host addresses on that port may be possible. Other examples, filesystem grants, and OpenShell remain unintegrated.

  ```sh
  dotnet publish examples/01-hello-world-chat-stdio.cs -o /tmp/local-agent-poc-chat
  printf 'Say hello.\n' | dotnet examples/mxc-chat.cs /tmp/local-agent-poc-chat/01-hello-world-chat-stdio
  ```

  The launcher requires a numeric IPv4 HTTP loopback endpoint and opens its configured port; publish the executable from the same generated template so its endpoint matches. Only use a trusted executable. The port exception is not an authentication mechanism; the local model service remains responsible for its own access controls.
- OpenShell 0.1.8 provides a standalone macOS CLI artifact, but its gateway and sandbox have not been provisioned or tested here. The [official installation](https://docs.nvidia.com/openshell/about/installation) sets up a gateway service; review its effects before installing. Wiring an oMLX endpoint into a sandbox is unresolved. OpenShell's Windows MXC backend is not a macOS integration.
- A denied-loopback probe cannot establish broad network or filesystem isolation. Do not treat it as certification.

Explore [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/), [MXC](https://github.com/microsoft/mxc), [OpenShell](https://docs.nvidia.com/openshell/), and [corven-agents](https://github.com/matthewcorven/corven-agents) for agent tools, approvals, and safety patterns. This standalone experiment is not an implementation of the corven-agents hub.
