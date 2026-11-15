using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;
using Serilog;

namespace AGUIWebChat.Server.Inference;

/// <summary>
/// Builds Foundry chat clients. Reasoning-capable deployments go through the OpenAI Responses
/// API because that is the only surface that returns reasoning summaries; everything else uses
/// the regular chat completions client.
/// </summary>
public sealed class FoundryChatClientFactory
{
    private readonly Func<Uri, AzureOpenAIClient> _clientFactory;

    public FoundryChatClientFactory(FoundryOptions options, Func<Uri, AzureOpenAIClient>? clientFactory = null)
    {
        Options = options;
        _clientFactory = clientFactory ?? CreateAzureClient;
    }

    /// <summary>The resolved Foundry settings this factory was built from.</summary>
    public FoundryOptions Options { get; }

    /// <summary>
    /// Creates a client for <paramref name="deploymentName"/>. <paramref name="useResponsesApi"/>
    /// selects the Responses surface, which is required for reasoning summaries.
    /// </summary>
    public IChatClient Create(string deploymentName, string? catalogueEndpoint, bool useResponsesApi)
    {
        Uri endpoint = Options.ResolveEndpoint(catalogueEndpoint);

        AzureOpenAIClient client = _clientFactory(endpoint);

        Log.Information(
            "SERVER FOUNDRY_CLIENT Endpoint={Endpoint} Deployment={Deployment} Surface={Surface}",
            endpoint,
            deploymentName,
            useResponsesApi ? "responses" : "chat");

        return useResponsesApi
            ? client.GetResponsesClient().AsIChatClient(deploymentName)
            : client.GetChatClient(deploymentName).AsIChatClient();
    }

    /// <summary>
    /// True when the deployment should use the Responses API. A catalogue model is reasoning
    /// capable when it declares a thinking mode or a reasoning-effort list.
    /// </summary>
    public static bool RequiresResponsesApi(Domain.AI.AIModel model)
    {
        return model.ThinkingMode != Contracts.AI.ThinkingMode.None || model.ReasoningEfforts.Count > 0;
    }

    private static AzureOpenAIClient CreateAzureClient(Uri endpoint)
    {
        // DefaultAzureCredential covers `az login` locally and managed identity when deployed.
        return new AzureOpenAIClient(endpoint, new DefaultAzureCredential());
    }
}
