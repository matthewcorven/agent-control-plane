using System.Net;
using System.Text;
using System.Text.Json;
using AGUIWebChat.Server.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class OmlxChatClientFactoryTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(x =>
            new KeyValuePair<string, string?>(x.Key, x.Value))).Build();

    [Theory]
    [InlineData("http://example.com:8001/v1")]
    [InlineData("http://localhost:8001/v1")]
    [InlineData("https://127.0.0.1:8001/v1")]
    [InlineData("http://127.0.0.1:8001/v1?key=value")]
    [InlineData("http://user:password@127.0.0.1:8001/v1")]
    [InlineData("http://127.0.0.1:8001/other")]
    [InlineData("http://127.0.0.1:8001/v1#fragment")]
    public void RejectsUnsafeEndpoint(string endpoint)
    {
        var factory = new OmlxChatClientFactory(Configuration());
        Assert.Throws<InvalidOperationException>(() => factory.Create("model", endpoint));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendsOpenAiChatCompletionsWithoutAuthorization(bool streaming)
    {
        var handler = new CaptureHandler(streaming);
        var factory = new OmlxChatClientFactory(Configuration(), () => handler);
        using var client = factory.Create("local-model", "http://127.0.0.1:8001/v1");
        string text;
        if (streaming)
        {
            var parts = new List<string>();
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken))
                parts.Add(update.Text);
            text = string.Concat(parts);
        }
        else
        {
            var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")],
                cancellationToken: TestContext.Current.CancellationToken);
            text = response.Text;
        }
        Assert.Equal("hi", text);
        Assert.Equal("/v1/chat/completions", handler.Uri!.AbsolutePath);
        Assert.False(handler.HasAuthorization);
        Assert.Equal("local-model", handler.Body.GetProperty("model").GetString());
        Assert.Equal("hello", handler.Body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(streaming, handler.Body.TryGetProperty("stream", out var stream) && stream.GetBoolean());
    }

    private sealed class CaptureHandler(bool streaming) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public bool HasAuthorization { get; private set; }
        public JsonElement Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            HasAuthorization = request.Headers.Contains("Authorization");
            Body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            string response = streaming
                ? "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"local-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"local-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                : "{\"id\":\"1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"local-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        }
    }
}
