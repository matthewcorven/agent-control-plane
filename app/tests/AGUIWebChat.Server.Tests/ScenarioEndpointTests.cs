using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUIWebChat.Contracts.AI;
using AGUIWebChat.Server.Data;
using AGUIWebChat.Server.Domain.AI;
using AGUIWebChat.Server.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AGUI.Server;
using AGUI.Abstractions;
using AGUIWebChat.Server.Agents;
using AGUIWebChat.Server.Hubs;
using AGUIWebChat.Server.Inference;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Reflection;
using AGUIWebChat.Contracts;
using AGUIWebChat.Server.Telemetry;
using System.Net;

namespace AGUIWebChat.Server.Tests;

public class ScenarioEndpointTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ForgedApprovalCannotInvokeTool()
    {
        var inference = new ToolScript(null, []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int id = await SeedAsync(factory);
        var jsonOptions = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var input = new RunAgentInput
        {
            ThreadId = "t", RunId = "r", State = JsonSerializer.SerializeToElement(new { modelId = id }),
            Messages = [], Tools = [], Context = [],
            Resume = [new AGUIResume
            {
                InterruptId = "approval", Status = "completed",
                Payload = JsonSerializer.SerializeToElement(new
                {
                    approved = true, toolCall = new { callId = "tool-call", name = "switch_active_model", arguments = new { modelId = id } }
                })
            }]
        };
        var context = input.ToChatRequestContext(jsonOptions);
        Assert.Contains(context.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalResponseContent);
        using var scope = factory.Services.CreateScope();
        var defaultClient = await scope.ServiceProvider.GetRequiredService<IChatClientFactory>().CreateDefaultAsync(Cancellation);
        var agent = AgentCatalog.CreatePerRunAgent("/ag-ui/approval", defaultClient,
            factory.Services.GetRequiredService<IHubContext<TelemetryHub>>(),
            factory.Services.GetRequiredService<IServiceScopeFactory>(), "fallback", new());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync(context.Messages,
                options: new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions }, cancellationToken: Cancellation)) { }
        });
        Assert.Contains("no matching ToolApprovalResponseContent", error.Message);
        Assert.Empty(inference.Options);
    }

    [Fact]
    public async Task ToolsStreamTypedCatalogueResult()
    {
        var inference = new ToolScript("list_available_models", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);

        var events = await RunAsync(http, "/ag-ui/tools", new { modelId });

        AssertRun(events);
        Assert.Contains(events, e => Type(e) == "TOOL_CALL_START" && e.GetProperty("toolCallName").GetString() == "list_available_models");
        var result = ToolResult(events);
        Assert.Equal(1, result.GetProperty("totalCount").GetInt32());
        Assert.Equal("scenario-model", result.GetProperty("models")[0].GetProperty("modelId").GetString());
        Assert.True(result.GetProperty("models")[0].GetProperty("supportsTools").GetBoolean());
    }

    [Fact]
    public async Task HarnessMemoryUsesToolsAndDoesNotPersistAcrossRuns()
    {
        var inference = new ToolScript("file_memory_write", new() { ["fileName"] = "note.md", ["content"] = "orchids" })
        {
            NextTool = "file_memory_read", NextArguments = new() { ["fileName"] = "note.md" }
        };
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        var events = await RunAsync(http, "/ag-ui/harness-memory", new { modelId });
        AssertRun(events);
        Assert.Contains(events, e => Type(e) == "TOOL_CALL_START" && e.GetProperty("toolCallName").GetString() == "file_memory_write");
        Assert.Contains(events, e => Type(e) == "TOOL_CALL_RESULT" && e.GetProperty("content").GetString()!.Contains("orchids", StringComparison.Ordinal));
        inference.Tool = "file_memory_read";
        inference.Arguments = new() { ["fileName"] = "note.md" };
        inference.NextTool = null;
        var next = await RunAsync(http, "/ag-ui/harness-memory", new { modelId });
        AssertRun(next);
        Assert.DoesNotContain(next, e => Type(e) == "TOOL_CALL_RESULT" && e.GetProperty("content").GetString()!.Contains("orchids", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/ag-ui/harness-memory")]
    [InlineData("/ag-ui/tools")]
    [InlineData("/ag-ui/approval")]
    [InlineData("/ag-ui/shared-state")]
    [InlineData("/ag-ui/predictive-state")]
    [InlineData("/ag-ui/generative-ui")]
    public async Task ToolEndpointsRejectModelsWithoutToolSupport(string endpoint)
    {
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory, supportsTools: false);

        var events = await RunAsync(http, endpoint, new { modelId });

        Assert.Contains(events, e => Type(e) == "RUN_ERROR");
        Assert.DoesNotContain(events, e => Type(e) == "RUN_FINISHED");
        Assert.Empty(inference.Options);
    }

    [Fact]
    public async Task GenerativeSnapshotPreservesSettingsAndDeltaIsJsonPatch()
    {
        var inference = new ToolScript("create_plan",
            new() { ["title"] = "Launch", ["steps"] = new[] { "Build", "Ship" } });
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);

        var events = await RunAsync(http, "/ag-ui/generative-ui",
            new { modelId, temperature = 0.25, telemetryChannel = "channel", custom = new { keep = true } });

        AssertRun(events);
        var state = Assert.Single(events, e => Type(e) == "STATE_SNAPSHOT").GetProperty("snapshot");
        Assert.Equal(0.25, state.GetProperty("temperature").GetDouble());
        Assert.True(state.GetProperty("custom").GetProperty("keep").GetBoolean());
        Assert.Equal("Launch", state.GetProperty("plan").GetProperty("title").GetString());
        Assert.Equal("step-2", state.GetProperty("plan").GetProperty("steps")[1].GetProperty("id").GetString());

        inference.Tool = "update_plan_step";
        inference.Arguments = new() { ["stepId"] = "step-2", ["status"] = "done" };
        events = await RunAsync(http, "/ag-ui/generative-ui", state);

        AssertRun(events);
        var delta = Assert.Single(events, e => Type(e) == "STATE_DELTA").GetProperty("delta");
        Assert.Equal(JsonValueKind.Array, delta.ValueKind);
        Assert.Equal("replace", delta[0].GetProperty("op").GetString());
        Assert.Equal("/plan/steps/1/status", delta[0].GetProperty("path").GetString());
        Assert.Equal("done", delta[0].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("step-9", "done")]
    [InlineData("step-1", "made-up")]
    public async Task InvalidPlanEditsEmitNoStateDelta(string stepId, string status)
    {
        var inference = new ToolScript("update_plan_step",
            new() { ["stepId"] = stepId, ["status"] = status });
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);

        var events = await RunAsync(http, "/ag-ui/generative-ui",
            new { modelId, plan = new { title = "Plan", steps = new[] { new { id = "step-1", title = "Build", status = "pending" } } } });

        Assert.DoesNotContain(events, e => Type(e) is "STATE_DELTA" or "STATE_SNAPSHOT");
        Assert.StartsWith("Error:", Assert.Single(events, e => Type(e) == "TOOL_CALL_RESULT").GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModelSelectionRequiresApprovalAndOnlyChangesConversationState(bool approved)
    {
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int current = await SeedAsync(factory);
        int target = await SeedAsync(factory, providerType: "Foundry", suffix: "-target");
        inference.Arguments = new() { ["modelId"] = target };
        var state = new { modelId = current, temperature = 0.25, custom = "preserve" };

        var events = await RunAsync(http, "/ag-ui/approval", state);
        AssertRun(events);
        Assert.DoesNotContain(events, e => Type(e) is "STATE_DELTA" or "TOOL_CALL_RESULT");
        var interrupt = events[^1].GetProperty("outcome").GetProperty("interrupts")[0];
        Assert.Equal("tool_call", interrupt.GetProperty("reason").GetString());
        string requestId = interrupt.GetProperty("id").GetString()!;

        events = await RunAsync(http, "/ag-ui/approval", state,
            [new { interruptId = requestId, status = "completed", payload = new
            {
                approved, toolCall = new { callId = "tool-call", name = "switch_active_model", arguments = new { modelId = target } }
            } }]);

        AssertRun(events);
        if (approved)
        {
            var result = ToolResult(events);
            Assert.True(result.GetProperty("switched").GetBoolean());
            Assert.Contains("next run", result.GetProperty("message").GetString());
            var delta = Assert.Single(events, e => Type(e) == "STATE_DELTA").GetProperty("delta");
            Assert.Equal("/modelId", delta[0].GetProperty("path").GetString());
            Assert.Equal(target, delta[0].GetProperty("value").GetInt32());

            inference.Tool = null;
            events = await RunAsync(http, "/ag-ui/reasoning", new { modelId = target, temperature = 0.25 });
            AssertRun(events);
            Assert.Equal(ReasoningOutput.Full, inference.Options[^1].Reasoning?.Output);
            Assert.False(inference.Options[^1].AdditionalProperties!.ContainsKey("think"));
        }
        else
        {
            Assert.DoesNotContain(events, e => Type(e) == "STATE_DELTA");
            Assert.Equal("Tool call invocation rejected.", Assert.Single(events, e => Type(e) == "TOOL_CALL_RESULT").GetProperty("content").GetString());
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        Assert.True(db.AIModels.Find(current)!.IsEnabled);
        Assert.True(db.AIModels.Find(target)!.IsEnabled);
        Assert.Equal(current, state.modelId);
        Assert.Equal(0.25, state.temperature);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("model-disabled")]
    [InlineData("provider-disabled")]
    [InlineData("no-tools")]
    public async Task InvalidApprovedTargetsNeverProduceStateChanges(string invalid)
    {
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int current = await SeedAsync(factory);
        int target = await SeedAsync(factory, suffix: "-target", supportsTools: invalid != "no-tools");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            if (invalid == "model-disabled") db.AIModels.Find(target)!.IsEnabled = false;
            if (invalid == "provider-disabled") db.AIProviders.Single(p => p.Name == "Scenario provider-target").IsEnabled = false;
            await db.SaveChangesAsync(Cancellation);
        }
        if (invalid == "missing") target = 9999;

        inference.Arguments = new() { ["modelId"] = target };
        var proposal = await RunAsync(http, "/ag-ui/approval", new { modelId = current });
        string requestId = proposal[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        var events = await RunAsync(http, "/ag-ui/approval", new { modelId = current },
            [new { interruptId = requestId, status = "completed", payload = new
            {
                approved = true, toolCall = new { callId = "tool-call", name = "switch_active_model", arguments = new { modelId = target } }
            } }]);

        Assert.DoesNotContain(events, e => Type(e) == "STATE_DELTA");
        Assert.StartsWith("Error:", Assert.Single(events, e => Type(e) == "TOOL_CALL_RESULT").GetProperty("content").GetString());
    }

    [Fact]
    public async Task ConcurrentSnapshotsDoNotShareConversationState()
    {
        var inference = new ToolScript("create_plan", new() { ["title"] = "Plan", ["steps"] = new[] { "Build" } });
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        var results = await Task.WhenAll(
            RunAsync(http, "/ag-ui/generative-ui", new { modelId, custom = "first", temperature = 0.1 }, threadId: "thread-first"),
            RunAsync(http, "/ag-ui/generative-ui", new { modelId, custom = "second", temperature = 0.9 }, threadId: "thread-second"));
        var first = Assert.Single(results[0], e => Type(e) == "STATE_SNAPSHOT").GetProperty("snapshot");
        var second = Assert.Single(results[1], e => Type(e) == "STATE_SNAPSHOT").GetProperty("snapshot");
        Assert.Equal("first", first.GetProperty("custom").GetString());
        Assert.Equal(0.1, first.GetProperty("temperature").GetDouble());
        Assert.Equal("second", second.GetProperty("custom").GetString());
        Assert.Equal(0.9, second.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModelDetailsDistinguishExistingAndMissingRows(bool exists)
    {
        var inference = new ToolScript("get_model_details", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        inference.Arguments = new() { ["modelId"] = exists ? modelId : 9999 };
        var result = ToolResult(await RunAsync(http, "/ag-ui/tools", new { modelId }));
        Assert.Equal(exists, result.GetProperty("found").GetBoolean());
        Assert.Equal(exists ? modelId : 9999, result.GetProperty("id").GetInt32());
        if (exists) Assert.Equal("Scenario model", result.GetProperty("displayName").GetString());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("status")]
    [InlineData("approved")]
    [InlineData("arguments")]
    public async Task MalformedResumeFailsBeforeSdkDecodingOrExecution(string invalid)
    {
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        inference.Arguments = new() { ["modelId"] = modelId };
        var proposal = await RunAsync(http, "/ag-ui/approval", new { modelId });
        string id = proposal[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        object[] malformed =
        [
            new
            {
                interruptId = invalid == "id" ? null : id,
                status = invalid == "status" ? "pending" : "completed",
                payload = new
                {
                    approved = invalid == "approved" ? (object)"yes" : true,
                    toolCall = new { callId = "tool-call", name = inference.Tool, arguments = invalid == "arguments" ? null : inference.Arguments }
                }
            }
        ];
        using var response = await PostAsync(http, "/ag-ui/approval", new { modelId }, malformed);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "approval_invalid_payload");
        var denied = await RunAsync(http, "/ag-ui/approval", new { modelId }, Resume(inference, id, approved: false));
        Assert.DoesNotContain(denied, e => Type(e) == "STATE_DELTA");
    }

    [Fact]
    public async Task ReasoningRejectsCatalogueModelsWithoutThinking()
    {
        var inference = new ToolScript(null, []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            db.AIModels.Find(modelId)!.ThinkingMode = ThinkingMode.None;
            await db.SaveChangesAsync(Cancellation);
        }
        var events = await RunAsync(http, "/ag-ui/reasoning", new { modelId });
        Assert.Equal("StreamingError", Assert.Single(events, e => Type(e) == "RUN_ERROR").GetProperty("code").GetString());
        Assert.Empty(inference.Options);
    }

    [Fact]
    public async Task PlanCreationAndUpdatesShareOnlyTheCurrentRunsState()
    {
        var inference = new ToolScript("create_plan", new() { ["title"] = "Plan", ["steps"] = new[] { "Build" } })
        {
            NextTool = "update_plan_step",
            NextArguments = new() { ["stepId"] = "step-1", ["status"] = "done" }
        };
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        var events = await RunAsync(http, "/ag-ui/generative-ui", new { modelId, custom = "preserved" });
        AssertRun(events);
        var snapshot = Assert.Single(events, e => Type(e) == "STATE_SNAPSHOT").GetProperty("snapshot");
        Assert.Equal("preserved", snapshot.GetProperty("custom").GetString());
        Assert.Equal("pending", snapshot.GetProperty("plan").GetProperty("steps")[0].GetProperty("status").GetString());
        var delta = Assert.Single(events, e => Type(e) == "STATE_DELTA").GetProperty("delta");
        Assert.Equal("/plan/steps/0/status", delta[0].GetProperty("path").GetString());
        Assert.Equal("done", delta[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task ConcurrentApprovedSelectionsRemainPerConversationOnSubsequentRuns()
    {
        var inference = new ToolScript("switch_active_model", [])
        {
            ArgumentSelector = options =>
            {
                Assert.True(options!.TryGetRunAgentInput(out var input));
                return new() { ["modelId"] = input.State!.Value.GetProperty("target").GetInt32() };
            }
        };
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int current = await SeedAsync(factory);
        int firstTarget = await SeedAsync(factory, suffix: "-first", providerType: "Foundry");
        int secondTarget = await SeedAsync(factory, suffix: "-second");
        var firstState = new { modelId = current, target = firstTarget, temperature = 0.1 };
        var secondState = new { modelId = current, target = secondTarget, temperature = 0.9 };
        var first = await RunAsync(http, "/ag-ui/approval", firstState, threadId: "first");
        var second = await RunAsync(http, "/ag-ui/approval", secondState, threadId: "second");
        string firstId = first[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        string secondId = second[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        var decisions = await Task.WhenAll(
            RunAsync(http, "/ag-ui/approval", firstState, Resume(new("switch_active_model", new() { ["modelId"] = firstTarget }), firstId), "first"),
            RunAsync(http, "/ag-ui/approval", secondState, Resume(new("switch_active_model", new() { ["modelId"] = secondTarget }), secondId), "second"));
        Assert.Equal(firstTarget, Assert.Single(decisions[0], e => Type(e) == "STATE_DELTA").GetProperty("delta")[0].GetProperty("value").GetInt32());
        Assert.Equal(secondTarget, Assert.Single(decisions[1], e => Type(e) == "STATE_DELTA").GetProperty("delta")[0].GetProperty("value").GetInt32());
        inference.Tool = null;
        await Task.WhenAll(
            RunAsync(http, "/ag-ui/reasoning", new { modelId = firstTarget, temperature = 0.1 }, threadId: "first"),
            RunAsync(http, "/ag-ui/reasoning", new { modelId = secondTarget, temperature = 0.9 }, threadId: "second"));
        var firstOptions = inference.Options.Last(options => options.TryGetRunAgentInput(out var input) && input.ThreadId == "first");
        var secondOptions = inference.Options.Last(options => options.TryGetRunAgentInput(out var input) && input.ThreadId == "second");
        Assert.Null(firstOptions.Temperature);
        Assert.Null(firstOptions.TopP);
        Assert.DoesNotContain(firstOptions.AdditionalProperties!.Keys,
            key => key is "temperature" or "top_p" or "think" or "top_k" or "num_ctx");
        Assert.Equal(0.9f, secondOptions.Temperature);
        Assert.Equal(0.9, secondOptions.AdditionalProperties!["temperature"]);
        Assert.Equal(ReasoningOutput.Full, firstOptions.Reasoning?.Output);
        Assert.Null(secondOptions.Reasoning);
        Assert.Equal(current, firstState.modelId);
        Assert.Equal(current, secondState.modelId);
    }

    [Theory]
    [InlineData(true, "New text")]
    [InlineData(true, "")]
    [InlineData(true, " ")]
    [InlineData(false, "New text")]
    public async Task PredictiveEditsOnlyEmitPatchAfterAcceptance(bool approved, string text)
    {
        var inference = new ToolScript("propose_document_edit", new()
        {
            ["title"] = "Revised", ["text"] = text, ["originalTitle"] = "Original", ["originalText"] = "Old text"
        });
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        var state = new { modelId, temperature = 0.25, document = new { title = "Original", text = "Old text" } };
        var proposal = await RunAsync(http, "/ag-ui/predictive-state", state);
        AssertRun(proposal);
        Assert.DoesNotContain(proposal, e => Type(e) is "STATE_DELTA" or "STATE_SNAPSHOT");
        string id = proposal[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;

        var events = await RunAsync(http, "/ag-ui/predictive-state", state, Resume(inference, id, approved));
        AssertRun(events);
        if (approved)
        {
            var patch = Assert.Single(events, e => Type(e) == "STATE_DELTA").GetProperty("delta");
            Assert.Equal("test", patch[0].GetProperty("op").GetString());
            Assert.Equal("/document", patch[0].GetProperty("path").GetString());
            Assert.Equal("Old text", patch[0].GetProperty("value").GetProperty("text").GetString());
            Assert.Equal("add", patch[1].GetProperty("op").GetString());
            Assert.Equal("Revised", patch[1].GetProperty("value").GetProperty("title").GetString());
            Assert.Equal(text, patch[1].GetProperty("value").GetProperty("text").GetString());
            Assert.Equal(patch.GetRawText(), ToolResult(events).GetRawText());
        }
        else
        {
            Assert.DoesNotContain(events, e => Type(e) == "STATE_DELTA");
        }
        Assert.Equal("Old text", state.document.text);
        Assert.Equal(0.25, state.temperature);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedDocumentRejectsMissingOrNullTextWithoutStateEvents(bool missing)
    {
        var arguments = new Dictionary<string, object?> { ["title"] = "Revised" };
        if (!missing) arguments["text"] = null;
        var inference = new ToolScript("update_document", arguments);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        var state = new { modelId, document = new { title = "Original", text = "Old text" } };

        var events = await RunAsync(http, "/ag-ui/shared-state", state);

        AssertRun(events);
        Assert.StartsWith("Error:", Assert.Single(events, e => Type(e) == "TOOL_CALL_RESULT").GetProperty("content").GetString());
        Assert.DoesNotContain(events, e => Type(e) is "STATE_SNAPSHOT" or "STATE_DELTA");
        Assert.Equal("Original", state.document.title);
        Assert.Equal("Old text", state.document.text);
    }

    [Theory]
    [InlineData("text", false)]
    [InlineData("text", true)]
    [InlineData("originalTitle", false)]
    [InlineData("originalTitle", true)]
    [InlineData("originalText", false)]
    [InlineData("originalText", true)]
    public async Task PredictiveDocumentRejectsMissingOrNullArgumentsAfterRealApproval(string argument, bool missing)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["title"] = "Revised", ["text"] = "New text", ["originalTitle"] = "Original", ["originalText"] = "Old text"
        };
        if (missing) arguments.Remove(argument);
        else arguments[argument] = null;
        var inference = new ToolScript("propose_document_edit", arguments);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var owner = factory.CreateClient();
        using var stranger = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        var state = new { modelId, document = new { title = "Original", text = "Old text" } };

        var proposal = await RunAsync(owner, "/ag-ui/predictive-state", state);
        AssertRun(proposal);
        Assert.DoesNotContain(proposal, e => Type(e) is "STATE_DELTA" or "STATE_SNAPSHOT" or "TOOL_CALL_RESULT");
        string id = proposal[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        Assert.NotEqual("tool-call", id);
        using (var crossCaller = await PostAsync(stranger, "/ag-ui/predictive-state", state, Resume(inference, id)))
            await AssertProblemAsync(crossCaller, HttpStatusCode.BadRequest, "approval_caller_missing");

        var events = await RunAsync(owner, "/ag-ui/predictive-state", state, Resume(inference, id));

        AssertRun(events);
        Assert.StartsWith("Error:", Assert.Single(events, e => Type(e) == "TOOL_CALL_RESULT").GetProperty("content").GetString());
        Assert.DoesNotContain(events, e => Type(e) is "STATE_SNAPSHOT" or "STATE_DELTA");
        Assert.Equal("Original", state.document.title);
        Assert.Equal("Old text", state.document.text);
        using var replay = await PostAsync(owner, "/ag-ui/predictive-state", state, Resume(inference, id));
        await AssertProblemAsync(replay, HttpStatusCode.Conflict, "approval_invalid_or_replayed");
    }

    [Fact]
    public async Task PredictiveProposalRejectsAChangedDocument()
    {
        var inference = new ToolScript("propose_document_edit", new()
        {
            ["title"] = "Revised", ["text"] = "New text", ["originalTitle"] = "Original", ["originalText"] = "Old text"
        });
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        var proposal = await RunAsync(http, "/ag-ui/predictive-state",
            new { modelId, document = new { title = "Original", text = "Old text" } });
        string id = proposal[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        var events = await RunAsync(http, "/ag-ui/predictive-state",
            new { modelId, document = new { title = "Original", text = "User edited this" } }, Resume(inference, id));
        Assert.DoesNotContain(events, e => Type(e) == "STATE_DELTA");
        Assert.StartsWith("Error:", Assert.Single(events, e => Type(e) == "TOOL_CALL_RESULT").GetProperty("content").GetString());
    }

    [Fact]
    public async Task ResumeIsBoundToCookieAndIsConsumedExactlyOnceUnderConcurrency()
    {
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var owner = factory.CreateClient();
        using var stranger = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        inference.Arguments = new() { ["modelId"] = modelId };
        var state = new { modelId };
        var proposal = await RunAsync(owner, "/ag-ui/approval", state);
        string id = proposal[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        object[] resume = Resume(inference, id);

        using (var missingCookie = await PostAsync(stranger, "/ag-ui/approval", state, resume))
            await AssertProblemAsync(missingCookie, HttpStatusCode.BadRequest, "approval_caller_missing");
        inference.Tool = null;
        await RunAsync(stranger, "/ag-ui/approval", state);
        inference.Tool = "switch_active_model";
        using (var crossCaller = await PostAsync(stranger, "/ag-ui/approval", state, resume))
            await AssertProblemAsync(crossCaller, HttpStatusCode.Conflict, "approval_invalid_or_replayed");

        var responses = await Task.WhenAll(
            PostAsync(owner, "/ag-ui/approval", state, resume),
            PostAsync(owner, "/ag-ui/approval", state, resume));
        using var accepted = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        using var rejected = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var events = await ReadEventsAsync(accepted);
        Assert.Single(events, e => Type(e) == "STATE_DELTA");
        await AssertProblemAsync(rejected, HttpStatusCode.Conflict, "approval_invalid_or_replayed");
        using var replay = await PostAsync(owner, "/ag-ui/approval", state, resume);
        await AssertProblemAsync(replay, HttpStatusCode.Conflict, "approval_invalid_or_replayed");
    }

    [Fact]
    public async Task ApprovalExpiresAfterThirtyMinutesOfInactivity()
    {
        var clock = new ManualTimeProvider();
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference, services => services.AddSingleton<TimeProvider>(clock));
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        inference.Arguments = new() { ["modelId"] = modelId };
        var state = new { modelId };
        var proposal = await RunAsync(http, "/ag-ui/approval", state);
        string id = proposal[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        clock.Advance(TimeSpan.FromMinutes(30));
        using var expired = await PostAsync(http, "/ag-ui/approval", state, Resume(inference, id));
        await AssertProblemAsync(expired, HttpStatusCode.Conflict, "approval_session_expired");
        Assert.Single(inference.Options);
    }

    [Fact]
    public async Task ActivityRefreshesExpiryButOldDecisionsCannotApproveReusedProviderCallIds()
    {
        var clock = new ManualTimeProvider();
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference, services => services.AddSingleton<TimeProvider>(clock));
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        inference.Arguments = new() { ["modelId"] = modelId };
        var state = new { modelId };
        var first = await RunAsync(http, "/ag-ui/approval", state);
        string firstId = first[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        clock.Advance(TimeSpan.FromMinutes(29));
        var denied = await RunAsync(http, "/ag-ui/approval", state, Resume(inference, firstId, approved: false));
        Assert.DoesNotContain(denied, e => Type(e) == "STATE_DELTA");
        clock.Advance(TimeSpan.FromMinutes(29));
        var second = await RunAsync(http, "/ag-ui/approval", state);
        string secondId = second[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        Assert.NotEqual(firstId, secondId);
        using var replay = await PostAsync(http, "/ag-ui/approval", state, Resume(inference, firstId));
        await AssertProblemAsync(replay, HttpStatusCode.Conflict, "approval_invalid_or_replayed");
        clock.Advance(TimeSpan.FromMinutes(29));
        var accepted = await RunAsync(http, "/ag-ui/approval", state, Resume(inference, secondId));
        Assert.Single(accepted, e => Type(e) == "STATE_DELTA");
    }

    [Fact]
    public async Task ApprovalCannotBeMovedToAnotherConversationWithTheSameProviderCallId()
    {
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        inference.Arguments = new() { ["modelId"] = modelId };
        var state = new { modelId };
        var first = await RunAsync(http, "/ag-ui/approval", state, threadId: "first");
        var second = await RunAsync(http, "/ag-ui/approval", state, threadId: "second");
        string firstId = first[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        string secondId = second[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        Assert.NotEqual(firstId, secondId);
        using var crossThread = await PostAsync(http, "/ag-ui/approval", state, Resume(inference, firstId), "second");
        await AssertProblemAsync(crossThread, HttpStatusCode.Conflict, "approval_invalid_or_replayed");
        using var missing = await PostAsync(http, "/ag-ui/approval", state, Resume(inference, firstId), "missing");
        await AssertProblemAsync(missing, HttpStatusCode.Conflict, "approval_session_missing");
    }

    [Fact]
    public async Task CancellationAfterDecisionConsumptionCannotMakeItExecutableAgain()
    {
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        inference.Arguments = new() { ["modelId"] = modelId };
        var state = new { modelId };
        var proposal = await RunAsync(http, "/ag-ui/approval", state);
        string id = proposal[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var reachedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inference.BeforeCompletion = async token =>
        {
            reachedCompletion.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        Task<HttpResponseMessage> request = PostAsync(http, "/ag-ui/approval", state, Resume(inference, id), requestCancellation: cancellation.Token);
        await reachedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        using var replay = await PostAsync(http, "/ag-ui/approval", state, Resume(inference, id));
        await AssertProblemAsync(replay, HttpStatusCode.Conflict, "approval_invalid_or_replayed");
        Assert.Equal(modelId, state.modelId);
    }

    [Fact]
    public async Task OversizedSessionEmitsTypedTerminalErrorAndDiscardsPendingAuthority()
    {
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        inference.Arguments = new()
        {
            ["modelId"] = modelId, ["padding"] = new string('x', ApprovalSessionStore.MaximumSessionBytes)
        };
        var events = await RunAsync(http, "/ag-ui/approval", new { modelId });
        var error = Assert.Single(events, e => Type(e) == "RUN_ERROR");
        Assert.Equal("approval_session_too_large", error.GetProperty("code").GetString());
        Assert.Equal("RUN_ERROR", Type(events[^1]));
        Assert.DoesNotContain(events, e => Type(e) == "RUN_FINISHED");
        using var missing = await PostAsync(http, "/ag-ui/approval", new { modelId },
            Resume(new ToolScript("switch_active_model", new() { ["modelId"] = modelId }), "unavailable"));
        await AssertProblemAsync(missing, HttpStatusCode.Conflict, "approval_session_missing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedInputFailsBeforeModelExecutionIncludingChunkedBodies(bool chunked)
    {
        var inference = new ToolScript(null, []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        await SeedAsync(factory);
        using HttpContent content = chunked
            ? new UnknownLengthContent(System.Text.Encoding.UTF8.GetBytes(new string('x', 64 * 1024 + 1)))
            : new StringContent(new string('x', 64 * 1024 + 1));
        content.Headers.ContentType = new("application/json");
        using var response = await http.PostAsync("/ag-ui/approval", content, Cancellation);
        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "approval_input_too_large");
        Assert.Empty(inference.Options);
    }

    [Fact]
    public async Task ApprovalStoreRejectsCapacityOverflowBeforeInference()
    {
        var inference = new ToolScript(null, []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        for (int index = 0; index < ApprovalSessionStore.Capacity; index++)
        {
            await RunAsync(http, "/ag-ui/approval", new { modelId }, threadId: $"capacity-{index}");
        }
        int calls = inference.Options.Count;
        using var overflow = await PostAsync(http, "/ag-ui/approval", new { modelId }, threadId: "capacity-overflow");
        await AssertProblemAsync(overflow, HttpStatusCode.ServiceUnavailable, "approval_store_full", restart: false);
        Assert.Equal(calls, inference.Options.Count);
    }

    [Fact]
    public async Task InvalidCallerCookieAndAlteredApprovalArgumentsFailExplicitly()
    {
        var inference = new ToolScript("switch_active_model", []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        inference.Arguments = new() { ["modelId"] = modelId };
        var proposal = await RunAsync(http, "/ag-ui/approval", new { modelId });
        string id = proposal[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
        inference.Arguments = new() { ["modelId"] = 9999 };
        using var altered = await PostAsync(http, "/ag-ui/approval", new { modelId }, Resume(inference, id));
        await AssertProblemAsync(altered, HttpStatusCode.Conflict, "approval_invalid_payload");
        using var raw = factory.CreateClient(new() { HandleCookies = false });
        raw.DefaultRequestHeaders.Add("Cookie", "agui-caller=forged");
        using var invalid = await PostAsync(raw, "/ag-ui/approval", new { modelId });
        await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "approval_caller_invalid");
        Assert.Single(inference.Options);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CookieIsHttpOnlyScopedAndSecureOnHttpsOrBehindProductionProxy(bool https, bool production)
    {
        using var factory = new AGUIWebChatWebApplicationFactory(() => new ToolScript(null, []));
        using var http = factory.CreateClient(new() { BaseAddress = new Uri(https ? "https://localhost" : "http://localhost") });
        if (production) factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>().EnvironmentName = "Production";
        int modelId = await SeedAsync(factory);
        using var response = await PostAsync(http, "/ag-ui/approval", new { modelId });
        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("agui-caller=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=none", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/ag-ui", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=1800", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationStopsTheActualAgentStream()
    {
        var inference = new ToolScript(null, []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);
        using var scope = factory.Services.CreateScope();
        var defaultClient = await scope.ServiceProvider.GetRequiredService<IChatClientFactory>().CreateDefaultAsync(Cancellation);
        var input = new RunAgentInput { State = JsonSerializer.SerializeToElement(new { modelId }), Messages = [], Tools = [], Context = [] };
        var jsonOptions = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var context = input.ToChatRequestContext(jsonOptions);
        var agent = AgentCatalog.CreatePerRunAgent("/ag-ui/tools", defaultClient,
            factory.Services.GetRequiredService<IHubContext<TelemetryHub>>(),
            factory.Services.GetRequiredService<IServiceScopeFactory>(), "fallback", new());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        await using var iterator = agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "Hello")],
            options: new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions }, cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await iterator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
    }

    private static object[] Resume(ToolScript script, string id, bool approved = true) =>
    [
        new { interruptId = id, status = "completed", payload = new
        {
            approved, toolCall = new { callId = "tool-call", name = script.Tool, arguments = script.Arguments }
        } }
    ];

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, bool restart = true)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(restart, json.RootElement.GetProperty("restartRequired").GetBoolean());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    public static IEnumerable<object[]> TelemetryCases()
    {
        foreach (string path in new[] { "", "/tools", "/approval", "/shared-state", "/predictive-state", "/generative-ui", "/reasoning" })
        {
            foreach (string provider in new[] { "Ollama", "Foundry" })
            {
                yield return ["/ag-ui" + path, provider];
            }
        }
    }

    [Theory]
    [MemberData(nameof(TelemetryCases))]
    public async Task EveryScenarioPreservesTelemetrySettingsAndProviderCapabilities(string endpoint, string provider)
    {
        var inference = new ToolScript(null, []);
        string channel = Guid.NewGuid().ToString("N");
        var published = new List<(string Method, object Payload)>();
        var destinations = new List<string>();
        var proxy = Proxy<IClientProxy>((method, args) =>
        {
            published.Add(((string)args[0]!, ((object[])args[1]!)[0]));
            return Task.CompletedTask;
        });
        var clients = Proxy<IHubClients>((method, args) =>
        {
            Assert.Equal("Group", method.Name);
            destinations.Add((string)args[0]!);
            return proxy;
        });
        var hub = Proxy<IHubContext<TelemetryHub>>((_, _) => clients);
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ReasoningTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (stopped) stopped.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference, services => services.AddSingleton(hub));
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory, providerType: provider);

        var events = await RunAsync(http, endpoint,
            new { modelId, telemetryChannel = channel, thinkingEffort = "high", temperature = 0.25, topP = 0.5, topK = 17, numCtx = 4096 });

        AssertRun(events);
        Assert.All(destinations, destination => Assert.Equal(channel, destination));
        var llm = Assert.IsType<LlmTelemetryDto>(Assert.Single(published, item => item.Method == "LlmMetricsCompleted").Payload);
        Assert.Equal("run-test", llm.RunId);
        Assert.Equal("scenario-model", llm.Model);
        Assert.Equal(provider, JsonSerializer.SerializeToElement(llm).GetProperty("Provider").GetString());
        if (endpoint == "/ag-ui") Assert.Equal("EnterpriseSupportAgent", llm.AgentName);
        else Assert.StartsWith("Scenario", llm.AgentName);
        Assert.Equal(3, llm.InputTokens);
        Assert.Equal(2, llm.OutputTokens);
        Assert.Equal(5, llm.TotalTokens);
        Assert.True(llm.RequestDurationMs > 0);
        var reasoning = Assert.IsType<ReasoningTelemetryDto>(Assert.Single(published, item => item.Method == "ReasoningCompleted").Payload);
        Assert.Equal("run-test", reasoning.RunId);
        Assert.Equal(1, reasoning.ChunkCount);
        Assert.Equal("Considering.".Length, reasoning.CharacterCount);
        Assert.Contains(stopped, activity => activity.GetTagItem("gen_ai.run.id") as string == "run-test"
            && activity.GetTagItem("gen_ai.request.model") as string == "scenario-model"
            && activity.GetTagItem("gen_ai.reasoning.effort") as string == "high");
        if (provider == "Ollama")
        {
            Assert.Equal(0.25f, inference.Options[0].Temperature);
            Assert.Equal(0.5f, inference.Options[0].TopP);
            Assert.Equal(0.25, inference.Options[0].AdditionalProperties!["temperature"]);
            Assert.Equal(0.5, inference.Options[0].AdditionalProperties!["top_p"]);
            Assert.Equal("high", inference.Options[0].AdditionalProperties!["think"]);
            Assert.Equal(17, inference.Options[0].AdditionalProperties!["top_k"]);
            Assert.Equal(4096, inference.Options[0].AdditionalProperties!["num_ctx"]);
        }
        else
        {
            Assert.Equal(ReasoningOutput.Full, inference.Options[0].Reasoning?.Output);
            Assert.Equal(ReasoningEffort.High, inference.Options[0].Reasoning?.Effort);
            Assert.Null(inference.Options[0].Temperature);
            Assert.Null(inference.Options[0].TopP);
            Assert.DoesNotContain(inference.Options[0].AdditionalProperties!.Keys,
                key => key is "temperature" or "top_p" or "think" or "top_k" or "num_ctx");
        }
    }

    [Theory]
    [MemberData(nameof(TelemetryCases))]
    public async Task EveryEndpointUsesServerDefaultsForInvalidSettings(string endpoint, string provider)
    {
        var inference = new ToolScript(null, []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory, providerType: provider);
        AssertRun(await RunAsync(http, endpoint, new
        {
            modelId, thinkingEffort = "extreme", temperature = 99, topP = -1, topK = 1001, numCtx = 131073
        }));
        var options = Assert.Single(inference.Options);
        if (provider == "Ollama")
        {
            Assert.Equal(0.7f, options.Temperature);
            Assert.Equal(0.9f, options.TopP);
            Assert.Equal("low", options.AdditionalProperties!["think"]);
            Assert.Equal(40, options.AdditionalProperties!["top_k"]);
            Assert.Equal(16384, options.AdditionalProperties!["num_ctx"]);
        }
        else
        {
            Assert.Equal(ReasoningEffort.Low, options.Reasoning?.Effort);
            Assert.Null(options.Temperature);
            Assert.Null(options.TopP);
        }
    }

    [Fact]
    public async Task MainChatResolvesProviderPerRunAndRetainsCanonicalRequestHistory()
    {
        var inference = new ToolScript(null, []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int ollama = await SeedAsync(factory, supportsTools: false);
        int foundry = await SeedAsync(factory, supportsTools: false, providerType: "Foundry", suffix: "-foundry");
        AssertRun(await RunAsync(http, "/ag-ui", new { modelId = ollama, temperature = 0.25 }));
        using var response = await http.PostAsJsonAsync("/ag-ui", new
        {
            threadId = "thread-test", runId = "run-next", state = new { modelId = foundry },
            messages = new object[]
            {
                new { id = "u1", role = "user", content = "First." },
                new { id = "a1", role = "assistant", content = "Previous." },
                new { id = "u2", role = "user", content = "Next." }
            },
            tools = Array.Empty<object>(), context = Array.Empty<object>()
        }, Cancellation);
        var events = await ReadEventsAsync(response);
        Assert.Equal("run-next", events[^1].GetProperty("runId").GetString());
        Assert.Equal("thread-test", events[^1].GetProperty("threadId").GetString());
        Assert.Equal(2, inference.Options.Count);
        Assert.Equal(0.25f, inference.Options[0].Temperature);
        Assert.Null(inference.Options[1].Temperature);
        Assert.NotNull(inference.Options[1].Reasoning);
        Assert.Equal(new[] { "First.", "Previous.", "Next." },
            inference.Messages[1].Where(message => message.Role != ChatRole.System).Select(message => message.Text));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(ollama, await scope.ServiceProvider.GetRequiredService<IChatClientFactory>().GetDefaultModelIdAsync(Cancellation));
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> callback) where T : class
    {
        var proxy = DispatchProxy.Create<T, TelemetryTests.Stub>();
        ((TelemetryTests.Stub)(object)proxy).Call = callback;
        return proxy;
    }

    [Theory]
    [InlineData("New text")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task SharedStateIsInjectedBeforeLastUserAndCanBeUpdated(string text)
    {
        var inference = new ToolScript("update_document", new() { ["title"] = "Revised", ["text"] = text });
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);

        var events = await RunAsync(http, "/ag-ui/shared-state",
            new { modelId, temperature = 0.25, document = new { title = "Original", text = "Old text" } });

        AssertRun(events);
        var messages = inference.Messages[0];
        Assert.Equal(ChatRole.User, messages[^1].Role);
        Assert.Equal(ChatRole.System, messages[^2].Role);
        Assert.Contains("Old text", messages[^2].Text);
        Assert.DoesNotContain("temperature", messages[^2].Text);
        var snapshot = Assert.Single(events, e => Type(e) == "STATE_SNAPSHOT").GetProperty("snapshot");
        Assert.Equal("Revised", snapshot.GetProperty("document").GetProperty("title").GetString());
        Assert.Equal(text, snapshot.GetProperty("document").GetProperty("text").GetString());
        Assert.Equal(snapshot.GetRawText(), ToolResult(events).GetRawText());
        Assert.Equal(0.25, snapshot.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData("/ag-ui")]
    [InlineData("/ag-ui/reasoning")]
    public async Task TextAndReasoningEndpointsKeepRunCorrelation(string endpoint)
    {
        var inference = new ToolScript(null, []);
        using var factory = new AGUIWebChatWebApplicationFactory(() => inference);
        using var http = factory.CreateClient();
        int modelId = await SeedAsync(factory);

        var events = await RunAsync(http, endpoint, new { modelId, thinkingEffort = "high", temperature = 0.25 });

        AssertRun(events);
        Assert.Contains(events, e => Type(e) == "REASONING_MESSAGE_CONTENT" && e.GetProperty("delta").GetString() == "Considering.");
        Assert.Contains(events, e => Type(e) == "TEXT_MESSAGE_CONTENT" && e.GetProperty("delta").GetString() == "Complete.");
        Assert.Equal("high", inference.Options[0].AdditionalProperties!["think"]);
        Assert.Equal(0.25, inference.Options[0].AdditionalProperties!["temperature"]);
    }

    private static void AssertRun(List<JsonElement> events)
    {
        Assert.Equal("RUN_STARTED", Type(events[0]));
        Assert.Equal("run-test", events[0].GetProperty("runId").GetString());
        Assert.Equal("thread-test", events[0].GetProperty("threadId").GetString());
        Assert.Equal("RUN_FINISHED", Type(events[^1]));
        Assert.Equal("run-test", events[^1].GetProperty("runId").GetString());
        Assert.DoesNotContain(events, e => Type(e) == "RUN_ERROR");
    }

    private static string? Type(JsonElement e) => e.GetProperty("type").GetString();

    private static JsonElement ToolResult(List<JsonElement> events)
    {
        var result = Assert.Single(events, e => Type(e) == "TOOL_CALL_RESULT").GetProperty("content");
        using var document = JsonDocument.Parse(result.GetString()!);
        return document.RootElement.Clone();
    }

    private static async Task<List<JsonElement>> RunAsync(HttpClient http, string endpoint, object state, object[]? resume = null, string threadId = "thread-test")
    {
        using var response = await PostAsync(http, endpoint, state, resume, threadId);
        response.EnsureSuccessStatusCode();
        return await ReadEventsAsync(response);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, string endpoint, object state, object[]? resume = null, string threadId = "thread-test",
        CancellationToken? requestCancellation = null) =>
        http.PostAsJsonAsync(endpoint, new
        {
            threadId, runId = "run-test", state,
            messages = new[] { new { id = "user-1", role = "user", content = "Do the scenario." } },
            tools = Array.Empty<object>(), context = Array.Empty<object>(), resume
        }, requestCancellation ?? Cancellation);

    private static async Task<List<JsonElement>> ReadEventsAsync(HttpResponseMessage response)
    {
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        string text = await response.Content.ReadAsStringAsync(Cancellation);
        return text.Split('\n')
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line[6..]);
                return document.RootElement.Clone();
            }).ToList();
    }

    private static async Task<int> SeedAsync(
        AGUIWebChatWebApplicationFactory factory, bool supportsTools = true, string providerType = "Ollama", string suffix = "")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var provider = new AIProvider { Name = "Scenario provider" + suffix, ProviderType = providerType, Endpoint = "https://example.openai.azure.com", IsEnabled = true };
        var model = new AIModel
        {
            Provider = provider, ModelId = "scenario-model" + suffix, DisplayName = "Scenario model" + suffix,
            SupportsTools = supportsTools, SupportsStreaming = true, ThinkingMode = ThinkingMode.Effort
        };
        db.AIModels.Add(model);
        await db.SaveChangesAsync(Cancellation);
        return model.Id;
    }

    private sealed class ToolScript(string? tool, Dictionary<string, object?> arguments) : IChatClient
    {
        public string? NextTool { get; set; }
        public Dictionary<string, object?> NextArguments { get; set; } = [];
        public Func<ChatOptions?, Dictionary<string, object?>>? ArgumentSelector { get; set; }
        public Func<CancellationToken, Task>? BeforeCompletion { get; set; }
        public string? Tool { get; set; } = tool;
        public Dictionary<string, object?> Arguments { get; set; } = arguments;
        public List<ChatOptions> Options { get; } = [];
        public List<List<ChatMessage>> Messages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("These tests exercise the streaming contract.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = messages.ToList();
            lock (Options)
            {
                Messages.Add(history);
                Options.Add(options!.Clone());
            }
            await Task.Yield();
            int resultCount = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count();
            if (Tool is not null && (resultCount == 0 || (resultCount == 1 && NextTool is not null)))
            {
                yield return new(ChatRole.Assistant, [new FunctionCallContent(
                    resultCount == 0 ? "tool-call" : "tool-call-2",
                    resultCount == 0 ? Tool : NextTool!,
                    resultCount == 0 ? ArgumentSelector?.Invoke(options) ?? Arguments : NextArguments)])
                {
                    MessageId = "tool-message", FinishReason = ChatFinishReason.ToolCalls
                };
            }
            else
            {
                if (BeforeCompletion is not null) await BeforeCompletion(cancellationToken);
                yield return new(ChatRole.Assistant, [new TextReasoningContent("Considering.")]) { MessageId = "answer" };
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(ChatRole.Assistant, "Complete.") { MessageId = "answer" };
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 2, TotalTokenCount = 5 })]);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
