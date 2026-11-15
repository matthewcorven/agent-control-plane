# Microsoft Foundry provider

The server can serve chat from Microsoft Foundry (Azure OpenAI) as well as from a local
Ollama instance. Provider selection is catalogue-driven: each `AIModel` row points at an
`AIProvider` row, and `AIProvider.ProviderType` decides which client the server builds.

| `AIProvider.ProviderType` | Client | Notes |
| --- | --- | --- |
| `Ollama` | `OllamaApiClient` | Default. Endpoint from `OLLAMA_ENDPOINT` / `OLLAMA_HOST`. |
| `Foundry` | `AzureOpenAIClient` | Endpoint from `AIProvider.Endpoint`, deployment from `AIModel.ModelId`. |

Any other value raises `UnsupportedProviderException` when that model is selected.

## Configuration

Foundry settings resolve from configuration first, then from environment variables. The
environment variables win, so a container or AppHost can override `appsettings.json`.

| Setting | Environment variable | Purpose |
| --- | --- | --- |
| `Foundry:Endpoint` | `FOUNDRY_URI` | Resource endpoint, e.g. `https://my-resource.openai.azure.com/`. Must be absolute https. |
| `Foundry:Model` | `FOUNDRY_MODEL` | Deployment name used when the catalogue has no Foundry model row. |
| `Foundry:ReasoningModel` | `FOUNDRY_REASONING_MODEL` | Reasoning-capable deployment. When set, the fallback uses the Responses API. |

Nothing is required for an Ollama-only deployment. Foundry is contacted only when a
Foundry catalogue model is selected, or when the catalogue is empty and `FOUNDRY_MODEL` /
`FOUNDRY_REASONING_MODEL` is set.

### Endpoint precedence

1. `AIProvider.Endpoint` on the selected catalogue row.
2. `Foundry:Endpoint` / `FOUNDRY_URI`.

If neither yields an absolute https URI, the request fails with
`FoundryNotConfiguredException` before any network call.

### Local setup

```bash
# Authenticate the developer identity used by DefaultAzureCredential.
az login

# Grant the signed-in user data-plane access to the resource.
az role assignment create \
  --assignee "$(az ad signed-in-user show --query id -o tsv)" \
  --role "Cognitive Services OpenAI User" \
  --scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<resource>"

# Store the endpoint in user secrets (never commit it).
dotnet user-secrets set "Foundry:Endpoint" "https://<resource>.openai.azure.com/" \
  --project src/Server/AGUIWebChat.Server.csproj
dotnet user-secrets set "Foundry:Model" "<deployment>" \
  --project src/Server/AGUIWebChat.Server.csproj
```

The AppHost forwards `Foundry:*` from its own configuration to the server as `FOUNDRY_*`
environment variables, and only when an endpoint is present. Set the same keys in the
AppHost's user secrets to use Foundry under `aspire run`:

```bash
dotnet user-secrets set "Foundry:Endpoint" "https://<resource>.openai.azure.com/" \
  --project src/AGUIWebChat.AppHost/AGUIWebChat.AppHost.csproj
```

### Deployed

`DefaultAzureCredential` resolves a managed identity in Azure. Assign the same
`Cognitive Services OpenAI User` role to that identity. No key or connection string is
read, so there is no secret to rotate.

## Reasoning

A Foundry model uses the Responses API when either condition holds:

- `AIModel.ThinkingMode` is not `None`, or
- the model declares at least one `AIModelReasoningEffort` row.

Otherwise the Chat Completions surface is used. Capabilities follow the selected model
and this same API-selection rule; deployment names are not used to infer capabilities.
When the selected model reports `SupportsReasoning`, `InferenceSettings.ThinkingEffort`
(`low` / `medium` / `high`) is
mapped to `ChatOptions.Reasoning` with `ReasoningOutput.Full`. Ollama models never receive
reasoning options; their `think` knob continues to travel as an additional property.

With no catalogue row, `Foundry:ReasoningModel` selects Responses and enables reasoning;
otherwise `Foundry:Model` selects Chat Completions without reasoning options. If both
settings are present, the reasoning deployment takes precedence.

## Sampling

Sampling-capable models receive `InferenceSettings.Temperature` and `TopP` through the
typed `ChatOptions.Temperature` and `ChatOptions.TopP` properties that the pinned OpenAI
adapters serialize. Foundry reasoning models receive neither sampling property.
Ollama retains its `temperature` / `top_p` additional properties and all existing
`think` / `top_k` / `num_ctx` knobs. Options from a previous selection are cleared before
the current model's settings are applied.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `FoundryNotConfiguredException: ... is not an absolute https URI` | Endpoint missing or `http://` | Set `Foundry:Endpoint` or the catalogue row's endpoint to an https URI. |
| `FoundryNotConfiguredException: Microsoft Foundry is not configured` | No endpoint anywhere | Set `Foundry:Endpoint` / `FOUNDRY_URI`. |
| `401` / `403` from Azure | Identity lacks the data-plane role | Assign `Cognitive Services OpenAI User` to the user or managed identity. |
| `404` from Azure | Deployment name does not exist | `AIModel.ModelId` must be the deployment name, not the model name. |
| `MissingMethodException` for `WebSearchToolResultContent.set_Results` | An older OpenAI adapter uses an API removed from the current AI abstractions | Use the compatible `Microsoft.Extensions.AI.OpenAI` `10.6.0` pin in the server `.csproj`. |
| `MissingMethodException` for `ResponsesClient` construction | A newer OpenAI SDK changed the constructor required by the Azure SDK | Keep the compatible adapter pin; do not independently upgrade the OpenAI SDK. |

The adapter pin is deliberate: `10.6.0` uses the current abstractions' `Outputs` API
and requires OpenAI `2.10.0`, which retains the Responses constructor used by
Azure.AI.OpenAI `2.9.0-beta.1`. The latest stable adapter (`10.11.0`) requires OpenAI
`2.14.0` and fails that constructor contract. The latest stable Azure SDK (`2.1.0`)
does not expose Responses, so the existing Azure beta remains necessary.
The next stable adapter after the pin (`10.7.0`) requires OpenAI `2.11.0` and already
fails the same Azure constructor contract.

## Tests

- `tests/AGUIWebChat.Server.Tests/ChatClientFactoryTests.cs` — routing, disabled-provider rejection, endpoint validation, reasoning surface selection.
- `tests/AGUIWebChat.Server.Tests/FoundryChatClientFactoryTests.cs` — `RequiresResponsesApi`, endpoint precedence, environment-vs-configuration precedence.
- `tests/AGUIWebChat.Server.Tests/ProviderInferenceCapabilitiesTests.cs` — capability matrix and reasoning option mapping.
- `tests/AGUIWebChat.Server.Tests/FoundryInferenceRequestTests.cs` — actual pinned SDK request JSON for catalogue selection, configuration-only defaults, and both OpenAI sampling adapters.

No test performs network I/O: a fake-key Azure client serializes requests into an
in-memory HTTP transport that captures the body and stops before network access.
Both API surfaces have streaming request-contract coverage. Responses streaming tests
also consume deterministic SSE reasoning and text updates through the actual adapter
for explicit catalogue selection, catalogue defaults, and configuration-only defaults.
This is not live Foundry network validation.
