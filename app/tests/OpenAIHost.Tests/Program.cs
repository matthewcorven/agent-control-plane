using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAIHost;

if (!HostWorkflow.IsEnabled(["--development-loopback"], "Development") ||
    HostWorkflow.IsEnabled([], "Development") || HostWorkflow.IsEnabled(["--development-loopback"], "Production") ||
    HostWorkflow.IsEnabled(["--development-loopback"], null) || HostWorkflow.IsEnabled(["--development-loopback", "extra"], "Development"))
    throw new Exception("Development opt-in failed closed incorrectly");

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var model = new FakeClient();
await using var app = HostWorkflow.Create(model, 0);
await app.StartAsync(cancellation.Token);
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    foreach (var (path, payload) in new[] {
        ("/v1/chat/completions", (object)new { model = "harness", messages = new[] { new { role = "user", content = "hello" } } }),
        ("/v1/responses", (object)new { model = "harness", input = "hello" }) })
    {
        using var result = await http.PostAsJsonAsync(path, payload, cancellation.Token);
        var body = await result.Content.ReadAsStringAsync(cancellation.Token);
        if (!result.IsSuccessStatusCode || !body.Contains("hello from model", StringComparison.Ordinal)) throw new Exception($"{path}: {result.StatusCode} {body}");
    }
    foreach (var header in new[] { "host", "origin" })
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonContent.Create(new { model = "harness" }) };
        if (header == "host") request.Headers.Host = "attacker.example";
        else request.Headers.Add("Origin", "http://localhost:1234");
        using var response = await http.SendAsync(request, cancellation.Token);
        if (response.StatusCode != HttpStatusCode.Forbidden) throw new Exception($"{header} was not rejected");
    }
    if (model.Calls != 2) throw new Exception("Rejected requests reached the model");
}
finally { await app.StopAsync(); }
Console.WriteLine("OpenAI host tests passed");

sealed class FakeClient : IChatClient
{
    public int Calls { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    { Calls++; return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello from model"))); }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { Calls++; await Task.Yield(); yield return new ChatResponseUpdate(ChatRole.Assistant, "hello from model"); }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
