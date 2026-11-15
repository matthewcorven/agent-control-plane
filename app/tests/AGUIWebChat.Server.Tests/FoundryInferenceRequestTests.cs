using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using AGUIWebChat.Contracts.AI;
using AGUIWebChat.Server.Domain.AI;
using AGUIWebChat.Server.Inference;
using AGUIWebChat.Server.Tests.Infrastructure;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Xunit;
using AGUI.Abstractions;
using AGUI.Server;
using AGUIWebChat.Contracts;
using AGUIWebChat.Server.Agents;
using AGUIWebChat.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AGUIWebChat.Server.Tests;

public class FoundryInferenceRequestTests
{
    private const string ResponseEvents = """
        event: response.created
        data: {"type":"response.created","sequence_number":0,"response":{"id":"resp_test","object":"response","created_at":1,"status":"in_progress","model":"reasoning-deployment","output":[]}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"rs_test","type":"reasoning","summary":[]}}

        event: response.reasoning_summary_text.delta
        data: {"type":"response.reasoning_summary_text.delta","sequence_number":2,"item_id":"rs_test","output_index":0,"summary_index":0,"delta":"Think first."}

        event: response.output_item.added
        data: {"type":"response.output_item.added","sequence_number":3,"output_index":1,"item":{"id":"msg_test","type":"message","role":"assistant","status":"in_progress","content":[]}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","sequence_number":4,"item_id":"msg_test","output_index":1,"content_index":0,"delta":"hello"}

        event: response.completed
        data: {"type":"response.completed","sequence_number":5,"response":{"id":"resp_test","object":"response","created_at":1,"status":"completed","model":"reasoning-deployment","output":[],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}


        """;

    private static readonly InferenceSettings Settings = new()
    {
        ThinkingEffort = "high",
        Temperature = 0.25,
        TopP = 0.75,
        TopK = 42,
        NumCtx = 8192
    };

    [Theory]
    [InlineData(false, ThinkingMode.None, false, false)]
    [InlineData(true, ThinkingMode.None, false, false)]
    [InlineData(false, ThinkingMode.OnOff, false, true)]
    [InlineData(true, ThinkingMode.OnOff, false, true)]
    [InlineData(false, ThinkingMode.Effort, true, true)]
    [InlineData(true, ThinkingMode.Effort, true, true)]
    [InlineData(false, ThinkingMode.None, true, true)]
    [InlineData(true, ThinkingMode.None, true, true)]
    public async Task CatalogueSelectionUsesSelectedModelsWireOptions(
        bool useDefault,
        ThinkingMode thinkingMode,
        bool hasReasoningEfforts,
        bool reasoning)
    {
        await using var database = await TestDbContextFactory.CreateAsync(TestContext.Current.CancellationToken);
        var provider = new AIProvider
        {
            Name = "Foundry",
            ProviderType = "Foundry",
            Endpoint = "https://catalogue.openai.azure.com/"
        };
        // Names deliberately conflict with capability metadata: names are not capability evidence.
        var model = new AIModel
        {
            Provider = provider,
            ModelId = reasoning ? "ordinary-deployment" : "reasoning-looking-deployment",
            DisplayName = "Selected model",
            ThinkingMode = thinkingMode,
            ReasoningEfforts = hasReasoningEfforts
                ? [new AIModelReasoningEffort { DisplayName = "High", Value = "high", IsDefault = true }]
                : []
        };
        database.DbContext.AIModels.Add(model);
        await database.DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.DbContext.ChangeTracker.Clear();

        using var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler);
        var foundry = CreateFoundryFactory(
            httpClient,
            new FoundryOptions("https://config.openai.azure.com/", "config-model", "config-reasoning"));
        var factory = new ChatClientFactory(database.DbContext, Configuration(), foundryFactory: foundry);

        IChatClient client;
        ProviderInferenceCapabilities capabilities;
        if (useDefault)
        {
            var selected = await factory.CreateDefaultAsync(TestContext.Current.CancellationToken);
            Assert.Equal(model.Id, selected.ModelId);
            client = selected.Client;
            capabilities = selected.Capabilities;
        }
        else
        {
            client = await factory.CreateAsync(model.Id, TestContext.Current.CancellationToken);
            capabilities = await factory.GetCapabilitiesAsync(model.Id, TestContext.Current.CancellationToken);
        }

        using (client)
        {
            await CaptureAsync(client, capabilities, streaming: true);
        }

        AssertRequest(handler, model.ModelId, reasoning, streaming: true);
        Assert.Equal("catalogue.openai.azure.com", handler.RequestUri!.Host);
    }

    [Theory]
    [InlineData("reasoning-looking-deployment", null, "reasoning-looking-deployment", false, false)]
    [InlineData("reasoning-looking-deployment", null, "reasoning-looking-deployment", false, true)]
    [InlineData(null, "ordinary-deployment", "ordinary-deployment", true, false)]
    [InlineData(null, "ordinary-deployment", "ordinary-deployment", true, true)]
    [InlineData("chat-deployment", "reasoning-deployment", "reasoning-deployment", true, false)]
    [InlineData("chat-deployment", "reasoning-deployment", "reasoning-deployment", true, true)]
    public async Task ConfigurationOnlyDefaultUsesSelectedDeploymentsWireOptions(
        string? model,
        string? reasoningModel,
        string expectedDeployment,
        bool reasoning,
        bool streaming)
    {
        await using var database = await TestDbContextFactory.CreateAsync(TestContext.Current.CancellationToken);
        using var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler);
        var foundry = CreateFoundryFactory(
            httpClient,
            new FoundryOptions("https://config.openai.azure.com/", model, reasoningModel));
        var factory = new ChatClientFactory(database.DbContext, Configuration(), foundryFactory: foundry);

        var selected = await factory.CreateDefaultAsync(TestContext.Current.CancellationToken);
        Assert.Null(selected.ModelId);
        using (selected.Client)
        {
            await CaptureAsync(selected.Client, selected.Capabilities, streaming);
        }

        AssertRequest(handler, expectedDeployment, reasoning, streaming);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamplingUsesTypedOptionsOnBothOpenAIAdapters(bool useResponsesApi)
    {
        using var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler);
        var foundry = CreateFoundryFactory(
            httpClient,
            new FoundryOptions("https://config.openai.azure.com/", null, null));
        using var client = foundry.Create("sampling-deployment", null, useResponsesApi);

        await CaptureAsync(client, ProviderInferenceCapabilities.For(ModelProviderKind.Foundry), streaming: false);

        AssertSampling(handler.Body);
        Assert.False(handler.Body.TryGetProperty("reasoning", out _));
        Assert.False(handler.Body.TryGetProperty("reasoning_effort", out _));
        Assert.EndsWith(useResponsesApi ? "/responses" : "/chat/completions", handler.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OllamaRetainsItsSerializedInferenceKnobs(bool streaming)
    {
        using var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") };
        using var client = new OllamaSharp.OllamaApiClient(httpClient, "ollama-deployment");

        await CaptureAsync(client, ProviderInferenceCapabilities.For(ModelProviderKind.Ollama), streaming);

        Assert.Equal("/api/chat", handler.RequestUri!.AbsolutePath);
        Assert.Equal("ollama-deployment", handler.Body.GetProperty("model").GetString());
        Assert.Equal("high", handler.Body.GetProperty("think").GetString());
        var options = handler.Body.GetProperty("options");
        AssertSampling(options);
        Assert.Equal(Settings.TopK, options.GetProperty("top_k").GetInt32());
        Assert.Equal(Settings.NumCtx, options.GetProperty("num_ctx").GetInt32());
        Assert.False(handler.Body.TryGetProperty("reasoning", out _));
        Assert.False(handler.Body.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponsesStreamingReadsActualReasoningAndTextUpdates(bool? useCatalogueDefault)
    {
        await using var database = await TestDbContextFactory.CreateAsync(TestContext.Current.CancellationToken);
        var model = new AIModel
        {
            Provider = new AIProvider
            {
                Name = "Foundry",
                ProviderType = "Foundry",
                Endpoint = "https://catalogue.openai.azure.com/"
            },
            ModelId = "reasoning-deployment",
            DisplayName = "Reasoning model",
            ThinkingMode = ThinkingMode.OnOff
        };
        if (useCatalogueDefault.HasValue)
        {
            database.DbContext.AIModels.Add(model);
            await database.DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            database.DbContext.ChangeTracker.Clear();
        }
        using var handler = new CaptureRequestHandler(ResponseEvents);
        using var httpClient = new HttpClient(handler);
        var foundry = CreateFoundryFactory(
            httpClient,
            new FoundryOptions("https://config.openai.azure.com/", null, "reasoning-deployment"));
        var factory = new ChatClientFactory(database.DbContext, Configuration(), foundryFactory: foundry);
        var selected = useCatalogueDefault == false
            ? new DefaultChatClient(
                await factory.CreateAsync(model.Id, TestContext.Current.CancellationToken),
                model.Id,
                await factory.GetCapabilitiesAsync(model.Id, TestContext.Current.CancellationToken))
            : await factory.CreateDefaultAsync(TestContext.Current.CancellationToken);
        using var client = selected.Client;
        var options = InferenceSettingsParser.ApplyInferenceSettings(null, Settings, selected.Capabilities).ChatOptions;
        List<ChatResponseUpdate> updates = [];

        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        AssertRequest(handler, "reasoning-deployment", reasoning: true, streaming: true);
        Assert.Equal("hello", string.Concat(updates.SelectMany(x => x.Contents).OfType<TextContent>().Select(x => x.Text)));
        Assert.Equal("Think first.", string.Concat(
            updates.SelectMany(x => x.Contents).OfType<TextReasoningContent>().Select(x => x.Text)));
        Assert.Contains(updates, x => x.ResponseId == "resp_test");
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().Build();

    [Theory]
    [InlineData(ModelProviderKind.Foundry)]
    [InlineData(ModelProviderKind.Ollama)]
    public async Task ActualProviderAdaptersPublishReasoningUsageAndTimings(ModelProviderKind provider)
    {
        const string ollamaEvents = """
            {"model":"reasoning-deployment","message":{"role":"assistant","content":"","thinking":"Think first."},"done":false}
            {"model":"reasoning-deployment","message":{"role":"assistant","content":"hello"},"done":true,"prompt_eval_count":3,"eval_count":2,"total_duration":2000000000,"load_duration":100000000,"prompt_eval_duration":400000000,"eval_duration":1000000000}

            """;
        using var handler = new CaptureRequestHandler(provider == ModelProviderKind.Foundry ? ResponseEvents : ollamaEvents);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") };
        using var client = provider == ModelProviderKind.Foundry
            ? CreateFoundryFactory(http, new("https://config.openai.azure.com/", null, null))
                .Create("reasoning-deployment", null, useResponsesApi: true)
            : (IChatClient)new OllamaSharp.OllamaApiClient(http, "reasoning-deployment");
        var sent = new List<(string Method, object Payload)>();
        var proxy = Proxy<IClientProxy>((_, args) =>
        {
            sent.Add(((string)args[0]!, ((object[])args[1]!)[0]));
            return Task.CompletedTask;
        });
        var input = new RunAgentInput
        {
            ThreadId = "provider-thread", RunId = "provider-run",
            State = JsonSerializer.SerializeToElement(new { telemetryChannel = "0123456789abcdef0123456789abcdef" }),
            Messages = [], Tools = [], Context = []
        };
        // The channel is validated by the real middleware before either observer runs.
        string channel = input.State!.Value.GetProperty("telemetryChannel").GetString()!;
        var hub = Proxy<IHubContext<TelemetryHub>>((_, _) => Proxy<IHubClients>((method, args) =>
        {
            Assert.Equal("Group", method.Name);
            Assert.Equal(channel, args[0]);
            return proxy;
        }));
        var agent = ChatAgentFactory.CreateAgent(client, "test", "Help.", "test-source",
            "reasoning-deployment", Settings, hub, ProviderInferenceCapabilities.For(provider, supportsReasoning: true));
        var context = input.ToChatRequestContext(new(JsonSerializerDefaults.Web));
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hello")],
            options: new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions },
            cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);
        Assert.Equal("hello", string.Concat(updates.SelectMany(u => u.Contents).OfType<TextContent>().Select(c => c.Text)));
        var reasoning = Assert.IsType<ReasoningTelemetryDto>(Assert.Single(sent, e => e.Method == "ReasoningCompleted").Payload);
        Assert.Equal("provider-run", reasoning.RunId);
        Assert.Equal("Think first.".Length, reasoning.CharacterCount);
        Assert.Equal(1, reasoning.ChunkCount);
        Assert.True(reasoning.DurationMs > 0);
        var metrics = Assert.IsType<LlmTelemetryDto>(Assert.Single(sent, e => e.Method == "LlmMetricsCompleted").Payload);
        Assert.Equal("provider-run", metrics.RunId);
        Assert.Equal(provider.ToString(), metrics.Provider);
        Assert.Equal("reasoning-deployment", metrics.Model);
        Assert.Equal((3L, 2L, 5L), (metrics.InputTokens, metrics.OutputTokens, metrics.TotalTokens));
        Assert.True(metrics.RequestDurationMs > 0);
        Assert.True(metrics.EndToEndTokensPerSecond > 0);
        if (provider == ModelProviderKind.Ollama)
        {
            Assert.Equal(2000, metrics.OllamaTotalDurationMs);
            Assert.Equal(100, metrics.LoadDurationMs);
            Assert.Equal(400, metrics.PromptEvalDurationMs);
            Assert.Equal(1000, metrics.EvalDurationMs);
            Assert.Equal(2, metrics.GenerationTokensPerSecond);
        }
        else Assert.Null(metrics.OllamaTotalDurationMs);
    }

    private static T Proxy<T>(Func<System.Reflection.MethodInfo, object?[], object?> callback) where T : class
    {
        var proxy = System.Reflection.DispatchProxy.Create<T, TelemetryTests.Stub>();
        ((TelemetryTests.Stub)(object)proxy).Call = callback;
        return proxy;
    }

    private static FoundryChatClientFactory CreateFoundryFactory(HttpClient httpClient, FoundryOptions options) =>
        new(options, endpoint => new AzureOpenAIClient(
            endpoint,
            new AzureKeyCredential("test-key"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(httpClient) }));

    private static async Task CaptureAsync(
        IChatClient client,
        ProviderInferenceCapabilities capabilities,
        bool streaming)
    {
        var options = Assert.IsType<ChatClientAgentRunOptions>(
            InferenceSettingsParser.ApplyInferenceSettings(null, Settings, capabilities)).ChatOptions;

        // Stop at the transport boundary, after the real pinned SDK serializes the request.
        await Assert.ThrowsAsync<RequestCapturedException>(async () =>
        {
            if (streaming)
            {
                await foreach (var _ in client.GetStreamingResponseAsync(
                    [new ChatMessage(ChatRole.User, "hello")],
                    options,
                    TestContext.Current.CancellationToken))
                {
                }
            }
            else
            {
                await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "hello")],
                    options,
                    TestContext.Current.CancellationToken);
            }
        });
    }

    private static void AssertRequest(
        CaptureRequestHandler handler,
        string deployment,
        bool reasoning,
        bool streaming)
    {
        var body = handler.Body;
        if (streaming)
        {
            Assert.True(body.GetProperty("stream").GetBoolean());
        }
        else
        {
            Assert.False(body.TryGetProperty("stream", out _));
        }
        Assert.False(body.TryGetProperty("think", out _));
        Assert.False(body.TryGetProperty("top_k", out _));
        Assert.False(body.TryGetProperty("num_ctx", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        if (reasoning)
        {
            Assert.EndsWith("/responses", handler.RequestUri!.AbsolutePath);
            Assert.Equal(deployment, body.GetProperty("model").GetString());
            Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal("detailed", body.GetProperty("reasoning").GetProperty("summary").GetString());
            Assert.False(body.TryGetProperty("temperature", out _));
            Assert.False(body.TryGetProperty("top_p", out _));
        }
        else
        {
            Assert.EndsWith($"/deployments/{deployment}/chat/completions", handler.RequestUri!.AbsolutePath);
            Assert.False(body.TryGetProperty("reasoning", out _));
            AssertSampling(body);
        }
    }

    private static void AssertSampling(JsonElement body)
    {
        Assert.Equal(Settings.Temperature, body.GetProperty("temperature").GetDouble());
        Assert.Equal(Settings.TopP, body.GetProperty("top_p").GetDouble());
    }

    private sealed class RequestCapturedException : Exception;

    private sealed class CaptureRequestHandler(string? responseStream = null) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public JsonElement Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = JsonSerializer.Deserialize<JsonElement>(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            if (responseStream is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseStream, Encoding.UTF8, "text/event-stream")
                };
            }
            throw new RequestCapturedException();
        }
    }
}
