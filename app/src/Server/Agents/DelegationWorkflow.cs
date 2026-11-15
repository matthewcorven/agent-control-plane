using System.Net;
using System.Security.Cryptography;
using System.Text;
using AGUIWebChat.Server.Inference;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AGUIWebChat.Server.Agents;

internal sealed record DelegationMessage(string Role, string Text);
internal sealed record DelegationActor(string Id, string Name, string Status, IReadOnlyList<DelegationMessage> Messages);
internal sealed record DelegationSnapshot(string Status, IReadOnlyList<DelegationActor> Actors);
internal sealed record DelegationStart(string Prompt);

// Each capability owns exactly one isolated, in-memory run. The run owns the client until all tools return.
internal sealed class DelegationWorkflow(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, TimeProvider clock) : IHostedService
{
    private const int Capacity = 8;
    private const int MaxChildren = 3;
    private readonly object gate = new();
    private readonly Dictionary<string, Run> runs = new(StringComparer.Ordinal);
    private bool stopping;
    private readonly CancellationTokenSource cleanupCancellation = new();
    private Task cleanup = Task.CompletedTask;

    private void RemoveExpired()
    {
        foreach (var key in runs.Where(x => x.Value.Execution.IsCompleted && x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray())
        {
            runs[key].Cancellation.Dispose();
            runs.Remove(key);
        }
    }

    private sealed class Actor(string id, string name)
    {
        internal string Id = id;
        internal string Name = name;
        internal string Status = "running";
        internal List<DelegationMessage> Messages = [];
    }
    private sealed class Run(CancellationToken shutdown, DateTimeOffset expires)
    {
        internal readonly object Gate = new();
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        internal readonly List<Actor> Actors = [];
        internal string Status = "running";
        internal DateTimeOffset Expires = expires;
        internal Task Execution = Task.CompletedTask;
    }

    internal string? Start(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 4000) return null;
        lock (gate)
        {
            RemoveExpired();
            if (stopping || runs.Count >= Capacity) return null;
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string keyHash = Hash(token);
            var run = new Run(lifetime.ApplicationStopping, clock.GetUtcNow().AddMinutes(15));
            run.Cancellation.CancelAfter(TimeSpan.FromMinutes(2));
            runs.Add(keyHash, run);
            run.Execution = ExecuteAsync(run, prompt.Trim());
            return token;
        }
    }

    internal DelegationSnapshot? Get(string? token)
    {
        Run? run = Find(token);
        if (run is null) return null;
        lock (run.Gate)
            return new(run.Status, run.Actors.Select(a => new DelegationActor(a.Id, a.Name, a.Status, a.Messages.ToArray())).ToArray());
    }

    internal bool Cancel(string? token)
    {
        lock (gate)
        {
            Run? run = Find(token);
            if (run is null) return false;
            run.Cancellation.Cancel();
            return true;
        }
    }

    private Run? Find(string? token)
    {
        if (token is not { Length: 64 } || !token.All(Uri.IsHexDigit)) return null;
        lock (gate)
        {
            if (!runs.TryGetValue(Hash(token), out var run) || run.Expires <= clock.GetUtcNow()) return null;
            return run;
        }
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Bounded(string? text) => text is null ? "" : text.Length <= 12000 ? text : text[..12000] + " [truncated]";

    private async Task ExecuteAsync(Run run, string prompt)
    {
        await Task.Yield();
        var coordinator = new Actor("coordinator", "Coordinator");
        lock (run.Gate) { coordinator.Messages.Add(new("user", prompt)); run.Actors.Add(coordinator); }
        try
        {
            string answer;
            using (var scope = scopes.CreateScope())
            {
            var configuration = scope.ServiceProvider.GetService<IConfiguration>();
            var environment = scope.ServiceProvider.GetService<IHostEnvironment>();
            bool scripted = environment?.IsDevelopment() == true
                && configuration?.GetValue<bool>("E2E:InMemoryDatabase") == true
                && configuration.GetValue<bool>("E2E:StubInference")
                && configuration.GetValue<bool>("E2E:DelegationScript");
            using var client = scripted ? new DelegationE2EChatClient() : (await scope.ServiceProvider.GetRequiredService<IChatClientFactory>().CreateDefaultAsync(run.Cancellation.Token)).Client;
            var child = client.AsHarnessAgent(Options("Specialist", "Help with the delegated task. Do not claim tools or external access."));
            async Task<string> Delegate(string task, CancellationToken cancellationToken)
            {
                if (string.IsNullOrWhiteSpace(task) || task.Length > 4000) return "Task must be 1–4000 characters.";
                Actor actor;
                lock (run.Gate)
                {
                    if (run.Actors.Count - 1 >= MaxChildren) return "Delegation limit reached.";
                    actor = new Actor(Guid.NewGuid().ToString("N"), "Specialist");
                    actor.Messages.Add(new("user", task));
                    run.Actors.Add(actor);
                }
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Cancellation.Token, cancellationToken);
                    var session = await child.CreateSessionAsync(linked.Token);
                    var response = await child.RunAsync(task, session, cancellationToken: linked.Token);
                    string text = Bounded(response.Text);
                    lock (run.Gate) { actor.Messages.Add(new("assistant", text)); actor.Status = "completed"; }
                    return text;
                }
                catch (OperationCanceledException) { lock (run.Gate) actor.Status = "cancelled"; throw; }
                catch (Exception) { lock (run.Gate) actor.Status = "failed"; return "The specialist failed; no result is available."; }
            }
            var options = Options("Coordinator", "Help with the user's task. When useful, delegate a bounded subtask using delegate_task and await its result. Never claim delegation that did not occur. You have no external tools.");
            options.ChatOptions!.Tools = [AIFunctionFactory.Create(Delegate, "delegate_task", "Delegate a task to a specialist and wait for its response.")];
            var agent = client.AsHarnessAgent(options);
            var session = await agent.CreateSessionAsync(run.Cancellation.Token);
            var result = await agent.RunAsync(prompt, session, cancellationToken: run.Cancellation.Token);
            answer = Bounded(result.Text);
            }
            lock (run.Gate) { coordinator.Messages.Add(new("assistant", answer)); coordinator.Status = "completed"; run.Status = "completed"; }
        }
        catch (OperationCanceledException) { lock (run.Gate) { coordinator.Status = "cancelled"; run.Status = "cancelled"; } }
        catch (Exception) { lock (run.Gate) { coordinator.Status = "failed"; run.Status = "failed"; } }
    }

    private static HarnessAgentOptions Options(string name, string instructions) => new()
    {
        Name = name, DisableFileMemory = true, DisableAgentSkillsProvider = true, DisableWebSearch = true,
        DisableToolAutoApproval = true, DisableOpenTelemetry = true, DisableTodoProvider = true,
        DisableAgentModeProvider = true, MaximumIterationsPerRequest = 4,
        ChatOptions = new ChatOptions { Instructions = instructions, MaxOutputTokens = 1024 }
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cleanup = CleanupAsync();
        return Task.CompletedTask;
    }
    private async Task CleanupAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cleanupCancellation.Token))
                lock (gate) RemoveExpired();
        }
        catch (OperationCanceledException) { }
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Run[] active;
        lock (gate)
        {
            stopping = true;
            active = runs.Values.ToArray();
            foreach (var run in active) run.Cancellation.Cancel();
        }
        cleanupCancellation.Cancel();
        await cleanup;
        await Task.WhenAll(active.Select(x => x.Execution));
        lock (gate) { foreach (var run in active) run.Cancellation.Dispose(); runs.Clear(); }
        cleanupCancellation.Dispose();
    }
}

internal static class DelegationEndpoints
{
    internal static IEndpointRouteBuilder MapDelegationWorkflow(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/delegation").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            string allowed = http.RequestServices.GetRequiredService<IConfiguration>()["Delegation:AllowedOrigin"] ?? "";
            if (!Uri.TryCreate(allowed, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https")
                || !origin.IsLoopback || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
                || !string.Equals(allowed.TrimEnd('/'), origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                || http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)
                || http.Request.Headers["X-Delegation-Request"] != "1"
                || !string.Equals(http.Request.Headers.Origin.ToString(), allowed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                || http.Request.QueryString.HasValue)
                return Results.StatusCode(403);
            return await next(context);
        });
        group.MapPost("/start", (DelegationStart input, DelegationWorkflow workflow) =>
        {
            if (string.IsNullOrWhiteSpace(input.Prompt) || input.Prompt.Length > 4000) return Results.BadRequest();
            string? capability = workflow.Start(input.Prompt);
            return capability is null ? Results.StatusCode(503) : Results.Ok(new { capability });
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16 * 1024));
        group.MapPost("/status", (HttpContext context, DelegationWorkflow workflow) =>
            workflow.Get(Token(context)) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapPost("/cancel", (HttpContext context, DelegationWorkflow workflow) =>
            workflow.Cancel(Token(context)) ? Results.Accepted() : Results.NotFound());
        return endpoints;
    }
    private static string? Token(HttpContext context)
    {
        string authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : null;
    }
}
