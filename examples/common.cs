#:package Microsoft.Agents.AI.OpenAI@1.28.0
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text;
using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;

static class LocalEndpoint
{
    public static Uri Create(string value)
    {
        var uri = new Uri(value);
        if (!uri.IsLoopback || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("The POC only accepts loopback HTTP(S) endpoints.");
        return uri;
    }
}

static class LocalHttp
{
    public static HttpClient Create(Uri endpoint)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        if (IPAddress.TryParse(endpoint.Host, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != endpoint.Port)
                    throw new HttpRequestException("Requests must stay on the configured local model endpoint.");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        }
        return new HttpClient(handler) { BaseAddress = endpoint };
    }
}

static class Agent
{
    private static readonly Uri Endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1");
    private static readonly HttpClient Http = LocalHttp.Create(Endpoint);

    public static async Task<string> Ask(string prompt, string model = "Qwen3.8-27B-oQ4e-mtp", string instructions = "Be concise and helpful.")
    {
        var client = new OpenAIClient(new ApiKeyCredential("local-no-auth"), new OpenAIClientOptions { Endpoint = Endpoint, Transport = new HttpClientPipelineTransport(Http) });
        var agent = client.GetChatClient(model).AsAIAgent(name: "Assistant", instructions: instructions);
        // Swap the previous agent for this one to add a little flair:
        // var agent = client.GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsAIAgent(name: "Pirate", instructions: "Answer helpfully like a dramatically polite pirate.");
        return (await agent.RunAsync(prompt)).Text;
    }
}

static class Audio
{
    private static readonly HttpClient Client = LocalHttp.Create(LocalEndpoint.Create("http://127.0.0.1:8001/v1/"));
    public static async Task Speak(string text, string path)
    {
        var payload = new JsonObject { ["model"] = "Ming-3.0-tts-tiny-4bit", ["input"] = text, ["response_format"] = "wav" };
        using var response = await Client.PostAsync("audio/speech", new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
    }
    public static async Task<string> Transcribe(string path)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("LFM3-Omni-2B-MLX-4bit"), "model");
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", Path.GetFileName(path));
        using var response = await Client.PostAsync("audio/transcriptions", form);
        response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("text").GetString() ?? throw new InvalidOperationException("No transcript returned.");
    }
}
