using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ProtocolWorkflows;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var stream = await LocalProtocols.A2AAsync("stream test", "stream", timeout.Token);
if (!stream.Events.Any(x => x.Contains("Discovered:")) || !stream.Events.Any(x => x.Contains("Artifact: Echo: stream test")) || !stream.Events.Any(x => x.Contains("Completed"))) throw new Exception("A2A streaming failed: " + string.Join(", ", stream.Events));
var background = await LocalProtocols.A2AAsync("background test", "background", timeout.Token);
if (!background.Events.Any(x => x.Contains("Started: Working")) || !background.Events.Any(x => x.Contains("Completed: Echo: background test"))) throw new Exception("A2A background failed: " + string.Join(", ", background.Events));
var mcp = await LocalProtocols.McpAsync(19, 23, timeout.Token);
if (!mcp.Events.Contains("Tools: add_numbers") || !mcp.Events.Contains("Result: 42")) throw new Exception("MCP failed: " + string.Join(", ", mcp.Events));
try { await LocalProtocols.A2AAsync("x", "https://example.com", timeout.Token); throw new Exception("Invalid mode accepted"); } catch (ArgumentException) { }

var web = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions { Args = [], EnvironmentName = "Development" });
web.WebHost.UseUrls("http://127.0.0.1:0");
web.Configuration["ProtocolWorkflows:AllowedOrigin"] = "http://localhost:5102";
await using var app = web.Build();
app.MapLocalProtocolWorkflows();
await app.StartAsync(timeout.Token);
try
{
    var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    using var denied = new HttpRequestMessage(HttpMethod.Post, "/api/local-protocols") { Content = System.Net.Http.Json.JsonContent.Create(new { Mode = "mcp", Left = 1, Right = 2 }) };
    denied.Headers.Add("Origin", "https://attacker.example"); denied.Headers.Add("X-Protocol-Workflow", "1");
    using var rejected = await http.SendAsync(denied, timeout.Token);
    if (rejected.StatusCode != System.Net.HttpStatusCode.Forbidden) throw new Exception("Untrusted origin accepted.");
    using var oversized = new HttpRequestMessage(HttpMethod.Post, "/api/local-protocols") { Content = System.Net.Http.Json.JsonContent.Create(new { Mode = "stream", Message = new string('x', 5000) }) };
    oversized.Headers.Add("Origin", "http://localhost:5102"); oversized.Headers.Add("X-Protocol-Workflow", "1");
    using var oversizedResult = await http.SendAsync(oversized, timeout.Token);
    if (oversizedResult.StatusCode != System.Net.HttpStatusCode.BadRequest) throw new Exception("Oversized chunked body accepted.");
    using var wrongHost = new HttpRequestMessage(HttpMethod.Post, "/api/local-protocols") { Content = System.Net.Http.Json.JsonContent.Create(new { Mode = "mcp" }) };
    wrongHost.Headers.Host = "attacker.example"; wrongHost.Headers.Add("Origin", "http://localhost:5102"); wrongHost.Headers.Add("X-Protocol-Workflow", "1");
    using var wrongHostResult = await http.SendAsync(wrongHost, timeout.Token);
    if (wrongHostResult.StatusCode != System.Net.HttpStatusCode.Forbidden) throw new Exception("Untrusted host accepted.");
    using var invalidMode = new HttpRequestMessage(HttpMethod.Post, "/api/local-protocols") { Content = System.Net.Http.Json.JsonContent.Create(new { Mode = "https://example.com" }) };
    invalidMode.Headers.Add("Origin", "http://localhost:5102"); invalidMode.Headers.Add("X-Protocol-Workflow", "1");
    using var invalidModeResult = await http.SendAsync(invalidMode, timeout.Token);
    if (invalidModeResult.StatusCode != System.Net.HttpStatusCode.BadRequest) throw new Exception("Invalid mode accepted.");
    using var allowed = new HttpRequestMessage(HttpMethod.Post, "/api/local-protocols") { Content = System.Net.Http.Json.JsonContent.Create(new { Mode = "mcp", Left = 19, Right = 23 }) };
    allowed.Headers.Add("Origin", "http://localhost:5102"); allowed.Headers.Add("X-Protocol-Workflow", "1");
    using var accepted = await http.SendAsync(allowed, timeout.Token);
    if (!accepted.IsSuccessStatusCode || !(await accepted.Content.ReadAsStringAsync(timeout.Token)).Contains("Result: 42")) throw new Exception("Allowed MCP endpoint failed.");
}
finally { await app.StopAsync(); }

Console.WriteLine("Protocol workflow tests passed");
