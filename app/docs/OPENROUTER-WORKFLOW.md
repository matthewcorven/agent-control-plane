# OpenRouter browser workflow

Navigate to `/openrouter` and explicitly submit a model ID, key, and prompt. The browser sends an OpenAI-compatible chat-completions request directly to `https://openrouter.ai/api/v1/chat/completions`. OpenRouter and its selected provider receive the prompt; charges and provider data policies apply. The application does not persist the key, send it through its own backend, or log it; it clears the password field on submission. Browser extensions and developer tools may still observe it. Responses are untrusted and displayed as text.

This is a direct browser HTTP client, **not** the Harness agent in example 11; it does not implement tool use, streaming, or AG-UI. It needs browser CORS permission from OpenRouter and has not been validated against a real provider account. Redirects are rejected, the destination is fixed, response size and request duration are bounded, and provider errors are not displayed. No remote endpoint URL can be configured from the form. Run `node app/tests/protocol/openrouter.test.mjs` from the POC root for the offline transport tests.

A2A discovery/HTTP+JSON/streaming/background, MCP tool hosting, and OpenAI-compatible hosting from examples 08–10 are not implemented by this workflow. Do not expose the separate unauthenticated example hosts publicly or proxy them without authentication and appropriate origin controls.
