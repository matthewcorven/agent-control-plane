using AGUIWebChat.Client;
using AGUIWebChat.Client.Components;
using AGUIWebChat.Client.Endpoints;
using Microsoft.AspNetCore.HttpLogging;
using Serilog;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults: health checks, OpenTelemetry, and service discovery.
builder.AddServiceDefaults();

// Use Serilog for logging requests and events
builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

// Use Microsoft Http logging traces
builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = HttpLoggingFields.None;

    logging.RequestBodyLogLimit = 64 * 1024;
    logging.ResponseBodyLogLimit = 64 * 1024;

    logging.CombineLogs = true;
});

string serverUrl = builder.Configuration["AGUI_SERVER_URL"] ?? "http://localhost:5100";

// First stratup
Log.Information("Starting AG-UI Web Chat Client");
Log.Information("Machine Name : {MachineName}", Environment.MachineName);
Log.Information("OS Version : {OSVersion}", Environment.OSVersion);
Log.Information("DotNet Version : {DotNetVersion}", Environment.Version);
Log.Information("UserName : {UserName}", Environment.UserName);
Log.Information("Process Id : {ProcessId}", Process.GetCurrentProcess().Id);
Log.Information("Process Name : {ProcessName}", Process.GetCurrentProcess().ProcessName);

// Auto selects Server initially and WebAssembly on later eligible visits.
// Both runtimes resolve the same services, so this host and the WebAssembly host
// (src/Client.Wasm/Program.cs) both call AddChatClientServices.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();
builder.Services.AddChatClientServices(serverUrl);
builder.Services.AddAgUiProxy(serverUrl);

var app = builder.Build();

// The WebAssembly host reads AGUI_SERVER_URL from wwwroot/appsettings.json, which is a static
// build-time asset. Rewrite it on the way out so the browser follows this host's configuration
// instead of a value baked in at build time (the two must agree, or the WASM phase calls the
// wrong backend on a later WebAssembly visit).
app.Use(async (context, next) =>
{
    if (!context.Request.Path.Equals("/appsettings.json", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    context.Response.ContentType = "application/json";
    context.Response.Headers.CacheControl = "no-store";

    await context.Response.WriteAsJsonAsync(new Dictionary<string, string>
    {
        ["AGUI_SERVER_URL"] = serverUrl
    });
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();
app.MapAgUiProxy();
app.MapDelegationProxy(serverUrl);
app.MapFileAgentProxy(serverUrl);

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    // typeof(_Imports).Assembly is the usual convention here, but the host and the
    // WebAssembly project both generate a Components._Imports type (CS0433), so the
    // assembly is resolved through a type that only exists in the WebAssembly project.
    .AddAdditionalAssemblies(typeof(AGUIWebChat.Client.ServiceCollectionExtensions).Assembly);

// Aspire ServiceDefaults: exposes /health and /alive for the dashboard health column.
app.MapDefaultEndpoints();

app.Run();
