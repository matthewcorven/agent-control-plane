# OpenAI-compatible host workflow

This standalone development host adapts a Harness agent to the SDK's OpenAI-compatible Chat Completions and Responses endpoints, corresponding to `examples/10-openai-host-harness.cs`. It is separate from the main web app and has no browser UI or shared registration. It uses the local model at `http://127.0.0.1:8001/v1` and model `Qwen3.8-27B-oQ4e-mtp`; start a compatible model server separately. The upstream client disables proxies and redirects. File memory, skills, web search, automatic tool approval and telemetry are disabled.

From the POC root, explicitly opt in:

```sh
DOTNET_ENVIRONMENT=Development dotnet run --project app/src/OpenAIHost/OpenAIHost.csproj -- --development-loopback
```

The service listens only on `127.0.0.1:5079`; it accepts loopback peers and loopback Host headers, rejects requests carrying Origin, and does not use CORS. Send OpenAI-compatible requests to `/v1/chat/completions` or `/v1/responses`, with model `harness`. This is **unauthenticated**: any local process can use it. Host/Origin checks are not authentication, and a malicious non-browser client can forge headers. Never proxy, publish, or expose this service to remote clients. It is not a production hosting configuration. Responses behavior is provided by the preview hosting SDK, not a claim of full OpenAI API compatibility.

Focused checks, using an in-process fake model and a real loopback listener:

```sh
dotnet run --project app/tests/OpenAIHost.Tests/OpenAIHost.Tests.csproj
```

The tests cover opt-in gating, both non-streaming APIs, and rejection of hostile Host and browser Origin headers; they do not require a live model and do not test every SDK feature. A live local oMLX Chat Completions request also returned a response; this does not establish full API compatibility. The app's OpenAI-compatible host page describes how to run it, but does not start it. This independent project is not registered on the main app listener.
