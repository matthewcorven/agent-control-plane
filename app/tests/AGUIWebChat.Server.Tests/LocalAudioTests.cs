using AGUIWebChat.Server.Audio;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public sealed class LocalAudioTests
{
    [Theory]
    [InlineData("http://example.com/v1/")]
    [InlineData("http://localhost/v1/")]
    [InlineData("http://127.0.0.1@evil.example/v1/")]
    [InlineData("file:///v1/")]
    [InlineData("http://127.0.0.1/v1/?redirect=elsewhere")]
    public void RejectsNonLocalOrAmbiguousEndpoints(string endpoint) =>
        Assert.Throws<InvalidOperationException>(() => LocalAudioEndpoints.Endpoint(endpoint));

    [Fact]
    public void AcceptsLoopbackAndNormalizesPath() =>
        Assert.Equal("http://127.0.0.1:8001/v1/", LocalAudioEndpoints.Endpoint("http://127.0.0.1:8001/v1").ToString());

    [Fact]
    public async Task BoundedReadsRejectOversizedStreams()
    {
        using var stream = new MemoryStream(new byte[5]);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalAudioEndpoints.ReadBoundedAsync(stream, 4, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidRequestsAreRejectedBeforeContactingModel()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        await using var app = builder.Build();
        app.MapLocalAudio();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var empty = await client.PostAsJsonAsync("/api/local-audio/speech", new { text = " " }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var longText = await client.PostAsJsonAsync("/api/local-audio/speech", new { text = new string('a', 4001) }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, longText.StatusCode);
        using var file = new ByteArrayContent("not wav data"u8.ToArray());
        file.Headers.ContentType = new("audio/wav");
        using var invalid = await client.PostAsync("/api/local-audio/transcribe", file, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    [Fact]
    public async Task ForwardsAudioProtocolToConfiguredLoopback()
    {
        var mockBuilder = WebApplication.CreateBuilder();
        mockBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var model = mockBuilder.Build();
        byte[] wav = "RIFF0000WAVE"u8.ToArray();
        model.MapPost("/v1/audio/speech", async (HttpRequest request) =>
        {
            using var json = await JsonDocument.ParseAsync(request.Body);
            Assert.Equal("speech-model", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("Hello", json.RootElement.GetProperty("input").GetString());
            Assert.Equal("wav", json.RootElement.GetProperty("response_format").GetString());
            return Results.File(wav, "audio/wav");
        });
        model.MapPost("/v1/audio/transcriptions", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            Assert.Equal("transcription-model", form["model"].ToString());
            var file = Assert.Single(form.Files);
            Assert.Equal("upload.wav", file.FileName);
            using var content = new MemoryStream();
            await file.CopyToAsync(content);
            Assert.Equal(wav, content.ToArray());
            return Results.Json(new { text = "Hello" });
        });
        await model.StartAsync(TestContext.Current.CancellationToken);
        var address = model.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["LocalAudio:Endpoint"] = address + "/v1/",
            ["LocalAudio:SpeechModel"] = "speech-model",
            ["LocalAudio:TranscriptionModel"] = "transcription-model"
        });
        await using var app = builder.Build();
        app.MapLocalAudio();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var speech = await client.PostAsJsonAsync("/api/local-audio/speech", new { text = "Hello" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, speech.StatusCode);
        Assert.Equal(wav, await speech.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        using var upload = new ByteArrayContent(wav);
        upload.Headers.ContentType = new("audio/wav");
        using var transcription = await client.PostAsync("/api/local-audio/transcribe", upload, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, transcription.StatusCode);
        Assert.Contains("Hello", await transcription.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

}
