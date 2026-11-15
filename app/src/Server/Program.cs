using AGUIWebChat.Middleware;
using AGUIWebChat.Server.Agents;
using AGUIWebChat.Server.Audio;
using AGUIWebChat.Server.Chess;
using AGUIWebChat.Server.Data;
using AGUIWebChat.Server.Endpoints;
using AGUIWebChat.Server.Hubs;
using AGUIWebChat.Server.Inference;
using AGUIWebChat.Server.Mapping;
using AGUIWebChat.Server.Middleware;
using AGUIWebChat.Server.Services.AI;
using AGUIWebChat.Server.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenTelemetry;
using OpenTelemetry.Trace;
using ProtocolWorkflows;
using Serilog;
using System.Diagnostics;
using System.Text.Json.Serialization;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults: health checks, OpenTelemetry, and service discovery.
builder.AddServiceDefaults();

// Chat clients are built per catalogue model so both local Ollama and Microsoft Foundry models
// are selectable. The factory owns endpoint resolution and provider routing.
builder.Services.AddScoped<IChatClientFactory, ChatClientFactory>();

// Microsoft Foundry is optional: the options resolve from configuration and the FOUNDRY_*
// environment variables, and the factory only fails when a Foundry catalogue model is
// actually selected. An Ollama-only deployment never touches Azure.
builder.Services.AddSingleton(FoundryOptions.Resolve(builder.Configuration));
builder.Services.AddSingleton<FoundryChatClientFactory>();

// Use Serilog for logging requests and events
builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

// Add SignalR for real-time communication
builder.Services.AddSignalR();

// CORS: since the Blazor WebAssembly (InteractiveAuto) client runs in the browser, its calls
// to this server are cross-origin. Loopback origins (any localhost port) are allowed in
// Development so the client's dev endpoints work without configuration; other environments
// list explicit origins in the Cors:AllowedOrigins configuration section. AllowCredentials
// is required by the SignalR /telemetry negotiate request.
const string ClientCorsPolicyName = "BlazorClient";
builder.Services.AddCors(options => options.AddPolicy(ClientCorsPolicyName, policy =>
{
    string[] allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

    policy
        .SetIsOriginAllowed(origin =>
            allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
            || (builder.Environment.IsDevelopment()
                && Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
                && uri.IsLoopback))
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
}));

// Add ProblemDetails for standardized error responses and custom exception handling
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// Use Microsoft Http logging traces
builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields =
        HttpLoggingFields.RequestMethod |
        HttpLoggingFields.RequestPath |
        HttpLoggingFields.RequestHeaders |
        HttpLoggingFields.RequestBody |
        HttpLoggingFields.ResponseStatusCode;

    logging.RequestBodyLogLimit = 64 * 1024;

    logging.MediaTypeOptions.AddText("application/json");

    logging.CombineLogs = true;
});

// First stratup
Log.Information("Starting AG-UI Web Chat Server");
Log.Information("Machine Name : {MachineName}", Environment.MachineName);
Log.Information("OS Version : {OSVersion}", Environment.OSVersion);
Log.Information("DotNet Version : {DotNetVersion}", Environment.Version);
Log.Information("UserName : {UserName}", Environment.UserName);
Log.Information("Process Id : {ProcessId}", Process.GetCurrentProcess().Id);
Log.Information("Process Name : {ProcessName}", Process.GetCurrentProcess().ProcessName);

// Add AGUI HTTP logging interceptor for logging HTTP requests and responses
builder.Services.AddHttpLoggingInterceptor<AgUiHttpLoggingInterceptor>();
builder.Services.AddSingleton<IAgUiSseEventLogger, AGUIWebChat.Server.Middleware.AgUiSseEventLogger>();

// Configure JSON serialization options to use string enums
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Add AGUI server services for hosting the AG-UI interface
builder.Services.AddAGUIServer();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChessWorkflow>();
builder.Services.AddSingleton<DelegationWorkflow>();
builder.Services.AddHostedService(services => services.GetRequiredService<DelegationWorkflow>());
builder.Services.AddSingleton<ApprovalCallerIsolationProvider>();
builder.Services.AddSingleton<ApprovalSessionStore>();
foreach (string name in new[] { "ScenarioApprovalAgent", "ScenarioPredictiveStateAgent" })
{
    builder.Services.AddKeyedSingleton<AgentSessionStore>(name, (services, _) =>
        new IsolationKeyScopedAgentSessionStore(services.GetRequiredService<ApprovalSessionStore>(),
            services.GetRequiredService<ApprovalCallerIsolationProvider>(), new() { Strict = true }));
}

// Add the database context for AI models 
if (!builder.Environment.IsEnvironment("Testing"))
{
    // E2E runs set E2E:InMemoryDatabase=true so the server needs no SQL Server container.
    // Tests own all data, so an in-memory store created per process is sufficient and
    // deterministic. The schema is created below because migrations are SQL Server-specific.
    var useInMemoryDatabase = builder.Configuration.GetValue<bool>("E2E:InMemoryDatabase");

    // Readiness reflects the catalogue database, not just process liveness: /health reports
    // the same remediation text the startup diagnostic prints. HealthCheckService resolves the
    // check inside a scope, so the scoped context is valid here.
    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("catalogue-database", tags: ["ready"]);

    builder.Services.AddDbContext<ChatDbContext>(options =>
    {
        if (useInMemoryDatabase)
        {
            options.UseInMemoryDatabase("aguiwebchat-e2e");

            return;
        }

        var connectionString =
            builder.Configuration.GetConnectionString("ChatDatabase")
            ?? throw new InvalidOperationException("Connection string 'ChatDatabase' not found.");

        options.UseSqlServer(connectionString);

        if (builder.Environment.IsDevelopment())
        {
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
        }
    });
}

// Register mapping configurations for AI models
MapsterExtensions.RegisterMappings();

// Register the AI model service for managing AI models
builder.Services.AddScoped<IAIModelService, AIModelService>();
builder.AddE2EChatScripts();

// Add OpenTelemetry (ReasoningTelemetry) tracing for the application
// Note: In production, consider using a more robust exporter (e.g., Jaeger, Zipkin, or Application Insights)
// For this example, we will use a console exporter for simplicity
// The tracing will be configured in the CreateTraceProviderConsole method below
WebApplication app = builder.Build();

using var enterpriseSupportTracerProvider = CreateTraceProviderConsole("EnterpriseSupportAgenceSource",
        ReasoningTelemetry.ActivitySourceName);

var telemetryHub = app.Services.GetRequiredService<IHubContext<TelemetryHub>>();

// E2E runs replace Ollama with a deterministic echo model so chat assertions are exact.
var stubInference = app.Configuration.GetValue<bool>("E2E:StubInference");

if (stubInference)
{
    Log.Warning("E2E stub inference enabled: chat responses are echoed, not generated.");
}

// Sanity-check the catalogue database before anything queries it. Aspire provisions an empty
// SQL Server and never applies migrations, so without this check a cold start dies inside the
// first query with a bare "Invalid object name 'AIModels'". The report is logged either way;
// a failure is fatal unless the operator explicitly opts into degraded startup.
bool tolerateDatabaseFailure = app.Configuration.GetValue<bool>("Database:AllowStartupWithoutCatalogue");

using (IServiceScope startupScope = app.Services.CreateScope())
{
    ChatDbContext sanityContext = startupScope.ServiceProvider.GetRequiredService<ChatDbContext>();

    DatabaseSanityReport databaseReport = await DatabaseSanityCheck.CheckAsync(sanityContext);

    if (!databaseReport.IsHealthy)
    {
        string diagnostic = databaseReport.ToDiagnosticText("FAILED");

        if (tolerateDatabaseFailure)
        {
            Log.Error(
                "{Diagnostic}Database:AllowStartupWithoutCatalogue=true, so startup continues. " +
                "Catalogue endpoints will fail until this is resolved.",
                diagnostic);
        }
        else
        {
            Log.Fatal("{Diagnostic}", diagnostic);

            throw new DatabaseNotReadyException(databaseReport);
        }
    }
    else
    {
        Log.Information("{Diagnostic}",
            databaseReport.ToDiagnosticText("PASSED"));

        foreach (string warning in databaseReport.Warnings)
        {
            Log.Warning("{Warning}", warning);
        }
    }
}

// The startup client supplies agent metadata; each run resolves its selected catalogue model.
DefaultChatClient defaultChatClient;

using (IServiceScope startupScope = app.Services.CreateScope())
{
    IChatClientFactory chatClientFactory = startupScope.ServiceProvider.GetRequiredService<IChatClientFactory>();

    defaultChatClient = await chatClientFactory.CreateDefaultAsync();
}

AIAgent enterpriseAgent = AgentCatalog.CreatePerRunAgent(
    "/ag-ui", defaultChatClient, telemetryHub,
    app.Services.GetRequiredService<IServiceScopeFactory>(),
    app.Configuration["OLLAMA_MODEL"] ?? "qwen3:1.7b",
    new InferenceSettings
    {
        ThinkingEffort = "low",
        Temperature = 0.7,
        TopP = 0.9,
        TopK = 40,
        NumCtx = 16384
    });

// Scenario agents: one per AG-UI pattern, all built through the same factory so telemetry,
// RunId correlation and provider capabilities stay identical to the main chat agent.
using var scenarioTracerProvider = CreateTraceProviderConsole(
    AgentCatalog.ToolsSource,
    AgentCatalog.ApprovalSource,
    AgentCatalog.SharedStateSource,
    AgentCatalog.PredictiveStateSource,
    AgentCatalog.GenerativeUiSource,
    AgentCatalog.ReasoningSource);

IServiceScopeFactory scenarioScopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
string scenarioModelName = app.Configuration["OLLAMA_MODEL"] ?? "qwen3:1.7b";
InferenceSettings scenarioSettings = new()
{
    ThinkingEffort = "low",
    Temperature = 0.7,
    TopP = 0.9,
    TopK = 40,
    NumCtx = 16384
};

app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), api =>
{
    api.UseExceptionHandler();
    api.UseStatusCodePages();
});

app.UseHttpLogging();

app.UseCors(ClientCorsPolicyName);
app.UseMiddleware<ApprovalCallerMiddleware>();

app.MapHub<TelemetryHub>("/telemetry");

app.UseMiddleware<SseLoggingMiddleware>();

app.MapAGUIServer("/ag-ui", enterpriseAgent);
foreach (string path in new[]
{
    "/ag-ui/tools", "/ag-ui/approval", "/ag-ui/shared-state",
    "/ag-ui/predictive-state", "/ag-ui/generative-ui", "/ag-ui/reasoning",
    "/ag-ui/harness-planning", "/ag-ui/harness-loop", "/ag-ui/harness-memory", "/ag-ui/harness-skills", "/ag-ui/harness-assistant"
})
{
    AIAgent scenarioAgent = AgentCatalog.CreatePerRunAgent(path, defaultChatClient, telemetryHub,
        scenarioScopeFactory, scenarioModelName, scenarioSettings);
    app.MapAGUIServer(path, scenarioAgent).WithMetadata(AgentCatalog.StreamOptions(path));
}

app.MapLocalAudio();
app.MapChessWorkflow();
app.MapDelegationWorkflow();
app.MapHarnessFileAgent();
app.MapLocalProtocolWorkflows();
app.MapAIModelEndpoints();
app.MapAIProviderEndpoints();
app.MapE2EChatScripts();

// Aspire ServiceDefaults: exposes /health and /alive for the dashboard health column.
app.MapDefaultEndpoints();

// The in-memory E2E database has no migrations, so create its schema and seed the
// provider/model rows the client needs on first paint.
if (app.Configuration.GetValue<bool>("E2E:InMemoryDatabase"))
{
    using var scope = app.Services.CreateScope();

    scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.EnsureCreated();
}

await app.RunAsync();

// Create a trace provider for console output
static TracerProvider CreateTraceProviderConsole(params string[] sourceNames)
{
    Log.Information("SERVER TRACE_PROVIDER console for {ActivitySources}",
        string.Join(", ", sourceNames));

    return Sdk.CreateTracerProviderBuilder()
        .AddSource(sourceNames)
        .SetSampler(new AlwaysOnSampler())
        .AddConsoleExporter()
        .Build();
}