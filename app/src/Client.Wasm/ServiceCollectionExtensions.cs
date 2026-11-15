using AGUIWebChat.Client.Middleware;
using AGUIWebChat.Client.Services;
using AGUIWebChat.Client.Services.AI;
using AGUIWebChat.Client.Services.Chess;
using AGUIWebChat.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace AGUIWebChat.Client;

/// <summary>
/// Shared DI registrations for the chat client components. Blazor Auto (InteractiveAuto)
/// selects Server initially and WebAssembly on later eligible visits. Both hosts call
/// this method to keep their rendering runtimes consistent.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatClientServices(this IServiceCollection services, string serverUrl)
    {
        services.AddFluentUIComponents();

        // TelemetryService owns a SignalR connection to the server's /telemetry hub.
        services.AddScoped(_ => new TelemetryService(new Uri(new Uri(serverUrl), "/telemetry").ToString()));

        // Add HttpClient for AIModelApiClient (/api/models)
        services.AddHttpClient<AIModelApiClient>(client =>
        {
            client.BaseAddress = new Uri(serverUrl);
        });

        // Add HttpClient for AIProviderApiClient (/api/providers/{id})
        services.AddHttpClient<AIProviderApiClient>(client =>
        {
            client.BaseAddress = new Uri(serverUrl);
        });

        services.AddHttpClient<ChessClient>(client => client.BaseAddress = new Uri(serverUrl));

        // AG-UI client SSE event logger
        services.AddSingleton<IAgUiSseEventLogger, AgUiClientSseEventLogger>();
        services.AddScoped<ChatSettingsService>();
        services.AddScoped<BrowserAgUiBridge>();
        services.AddScoped<UiAgentChatService>();

        return services;
    }
}
