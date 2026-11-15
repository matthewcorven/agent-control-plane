using AGUIWebChat.Server.Data;
using AGUIWebChat.Server.Inference;

namespace AGUIWebChat.Server.Endpoints;

public static class E2EChatEndpoints
{
    public static void AddE2EChatScripts(this WebApplicationBuilder builder)
    {
        if (!Enabled(builder.Environment, builder.Configuration)) return;

        builder.Services.AddSingleton<E2EChatScripts>();
        builder.Services.AddScoped<IChatClientFactory>(services => new ChatClientFactory(
            services.GetRequiredService<ChatDbContext>(),
            builder.Configuration,
            stubFactory: () => new ScriptedChatClient(services.GetRequiredService<E2EChatScripts>()),
            foundryFactory: services.GetRequiredService<FoundryChatClientFactory>()));
    }

    public static void MapE2EChatScripts(this WebApplication app)
    {
        if (!Enabled(app.Environment, app.Configuration)) return;

        var group = app.MapGroup("/api/e2e/chat-scripts");
        group.MapPost("/", (E2EChatStep[] steps, E2EChatScripts scripts) =>
        {
            if (steps.Length is < 1 or > 32 ||
                steps.Any(step => step is null || string.IsNullOrWhiteSpace(step.MessageId) ||
                    (step.Text is null && step.Error is null && step.Tool is null && step.Reasoning is null && !step.WaitForRelease)
                    || (step.Tool is not null && step.Arguments is not { ValueKind: System.Text.Json.JsonValueKind.Object })))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["steps"] = ["Provide 1 to 32 steps with a message ID and text, error, or release checkpoint."]
                });
            }

            string id = scripts.Add(steps);
            return Results.Ok(new { id, prompt = E2EChatScripts.PromptPrefix + id });
        });
        group.MapGet("/{id}", (string id, E2EChatScripts scripts) =>
            scripts.Status(id) is { } status ? Results.Ok(status) : Results.NotFound());
        group.MapPost("/{id}/release", (string id, E2EChatScripts scripts) =>
            scripts.Release(id) ? Results.NoContent() : Results.NotFound());
        group.MapDelete("/{id}", (string id, E2EChatScripts scripts) =>
            scripts.Remove(id) ? Results.NoContent() : Results.NotFound());
    }

    private static bool Enabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() &&
        configuration.GetValue<bool>("E2E:StubInference") &&
        configuration.GetValue<bool>("E2E:InMemoryDatabase");
}
