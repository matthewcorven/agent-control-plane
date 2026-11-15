var builder = DistributedApplication.CreateBuilder(args);

// Ollama runs natively (Homebrew) with Metal GPU acceleration, so it is modelled as
// an already-present local executable rather than an Aspire-managed container.
var ollama = builder.AddOllamaLocal("ollama");

var chatModel = ollama.AddModel("chat", "qwen3:1.7b");

// Aspire owns SQL Server for the AI model catalogue. Aspire waits for the server to
// pass its health check before injecting the connection string. The database resource
// name must match the ConnectionStrings key the server reads (ChatDatabase).
var sqlServer = builder.AddSqlServer("sql")
    .WithLifetime(ContainerLifetime.Persistent);

var chatDb = sqlServer.AddDatabase("ChatDatabase");

// AG-UI server: gets the injected connection string plus the Ollama model.
// AddOllamaLocal already injects OLLAMA_HOST (the resolved host endpoint) into every
// resource that references the executable, so OLLAMA_ENDPOINT is left to the app's own
// configuration and the AppHost does not overwrite it with a scheme-less host name.
//
// Encrypt=False keeps the local container connection plaintext. The Aspire-provisioned
// SQL Server image has no certificate the client trusts, and the default encryption
// handshake fails with "pre-login handshake" errors.
//
// The database schema and seed data are NOT applied automatically. Run once per fresh
// container:  dotnet ef database update --project src/Server/AGUIWebChat.Server.csproj
var server = builder.AddProject<Projects.AGUIWebChat_Server>("server")
    .WithReference(chatDb)
    .WaitFor(chatDb)
    .WithEnvironment("OLLAMA_MODEL", chatModel.Resource.ModelName)
    .WithEnvironment("ConnectionStrings__ChatDatabase", $"{chatDb.Resource.ConnectionStringExpression};Encrypt=False")
    .WithHttpEndpoint(port: 5100, name: "http")
    .WithExternalHttpEndpoints();

// Microsoft Foundry is optional. When Foundry:Endpoint (or FOUNDRY_URI) is present in the
// AppHost's own configuration — user secrets, appsettings, or the environment — the values are
// forwarded to the server. Without them the server runs Ollama-only and never contacts Azure.
string? foundryEndpoint = builder.Configuration["Foundry:Endpoint"] ?? builder.Configuration["FOUNDRY_URI"];
string? foundryModel = builder.Configuration["Foundry:Model"] ?? builder.Configuration["FOUNDRY_MODEL"];
string? foundryReasoningModel = builder.Configuration["Foundry:ReasoningModel"] ?? builder.Configuration["FOUNDRY_REASONING_MODEL"];

if (!string.IsNullOrWhiteSpace(foundryEndpoint))
{
    server = server.WithEnvironment("FOUNDRY_URI", foundryEndpoint);

    if (!string.IsNullOrWhiteSpace(foundryModel))
    {
        server = server.WithEnvironment("FOUNDRY_MODEL", foundryModel);
    }

    if (!string.IsNullOrWhiteSpace(foundryReasoningModel))
    {
        server = server.WithEnvironment("FOUNDRY_REASONING_MODEL", foundryReasoningModel);
    }
}

// Blazor client: AGUI_SERVER_URL is injected from the server's http endpoint so the
// client keeps its existing configuration key while following the AppHost.
builder.AddProject<Projects.AGUIWebChat_Client>("client")
    .WithReference(server)
    .WaitFor(server)
    .WithEnvironment("AGUI_SERVER_URL", new EndpointReference(server.Resource, "http"))
    .WithHttpsEndpoint(port: 7219, name: "https")
    .WithExternalHttpEndpoints();

builder.Build().Run();
