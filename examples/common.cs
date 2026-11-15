#:package Microsoft.Agents.AI.OpenAI@1.28.0
using System.ClientModel;
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

static class Agent
{
    public static async Task<string> Ask(string prompt)
    {
        var client = new OpenAIClient(new ApiKeyCredential("local-no-auth"), new OpenAIClientOptions { Endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1") });
        var agent = client.GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsAIAgent(name: "Assistant", instructions: "Be concise and helpful.");
        // Swap the previous agent for this one to add a little flair:
        // var agent = client.GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsAIAgent(name: "Pirate", instructions: "Answer helpfully like a dramatically polite pirate.");
        return (await agent.RunAsync(prompt)).Text;
    }
}

static class Audio
{
    private static readonly HttpClient Client = new() { BaseAddress = LocalEndpoint.Create("http://127.0.0.1:8001/v1/") };
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
