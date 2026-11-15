using AGUIWebChat.Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// The browser calls the AG-UI server cross-origin, so this must be a real server URL
// (not the host-relative BaseAddress). It mirrors the server-rendered host's
// AGUI_SERVER_URL key; the value is baked into wwwroot/appsettings.json at build time.
string serverUrl = builder.Configuration["AGUI_SERVER_URL"] ?? "http://localhost:5100";

// Blazor Auto renders the same components first from the server host and then from
// WebAssembly once the runtime loads, so both Program.cs files register the same
// services through this shared method to keep both phases consistent.
builder.Services.AddChatClientServices(serverUrl);

await builder.Build().RunAsync();
