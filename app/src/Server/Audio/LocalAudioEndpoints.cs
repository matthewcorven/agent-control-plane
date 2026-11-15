using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;

namespace AGUIWebChat.Server.Audio;

internal static class LocalAudioEndpoints
{
    internal const int MaxAudioBytes = 10 * 1024 * 1024;
    internal const int MaxTextLength = 4000;

    internal static Uri Endpoint(string? value)
    {
        if (!Uri.TryCreate(value ?? "http://127.0.0.1:8001/v1/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Audio endpoint must be an HTTP(S) loopback IP address without credentials, query or fragment.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal static bool IsWave(byte[] bytes) => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8);

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("Audio exceeds the 10 MiB limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    internal static void MapLocalAudio(this WebApplication app)
    {
        app.MapPost("/api/local-audio/transcribe", async (HttpRequest request, IConfiguration config, CancellationToken ct) =>
        {
            if (request.ContentLength > MaxAudioBytes || !string.Equals(request.ContentType, "audio/wav", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Upload a WAV file of at most 10 MiB.");
            try
            {
                var bytes = await ReadBoundedAsync(request.Body, MaxAudioBytes, ct);
                if (!IsWave(bytes)) return Results.BadRequest("The file is not a WAV file.");
                using var client = CreateClient(config);
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(config["LocalAudio:TranscriptionModel"] ?? "LFM3-Omni-2B-MLX-4bit"), "model");
                using var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "upload.wav");
                using var response = await client.PostAsync("audio/transcriptions", form, ct);
                if (!response.IsSuccessStatusCode) return Results.Problem($"Local transcription returned HTTP {(int)response.StatusCode}.", statusCode: 502);
                var jsonBytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), 1024 * 1024, ct);
                using var json = JsonDocument.Parse(jsonBytes);
                if (!json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    return Results.Problem("Local transcription returned no text.", statusCode: 502);
                return Results.Ok(new { text = text.GetString() });
            }
            catch (InvalidDataException) { return Results.Problem("Audio or transcription response exceeds the size limit.", statusCode: 413); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
            { return Results.Problem("Local transcription failed. Check the endpoint and model configuration.", statusCode: 502); }
        }).WithHttpLogging(HttpLoggingFields.None);

        app.MapPost("/api/local-audio/speech", async (SpeechRequest? input, IConfiguration config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input?.Text) || input.Text.Length > MaxTextLength)
                return Results.BadRequest("Enter 1–4000 characters of text.");
            try
            {
                using var client = CreateClient(config);
                using var response = await client.PostAsJsonAsync("audio/speech", new { model = config["LocalAudio:SpeechModel"] ?? "Ming-3.0-tts-tiny-4bit", input = input.Text, response_format = "wav" }, ct);
                if (!response.IsSuccessStatusCode) return Results.Problem($"Local speech generation returned HTTP {(int)response.StatusCode}.", statusCode: 502);
                var audio = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), MaxAudioBytes, ct);
                if (!IsWave(audio)) return Results.Problem("Local speech generation did not return WAV audio.", statusCode: 502);
                return Results.File(audio, "audio/wav");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException or TaskCanceledException)
            { return Results.Problem("Local speech generation failed. Check the endpoint and model configuration.", statusCode: 502); }
        }).WithMetadata(new RequestSizeLimitAttribute(65536)).WithHttpLogging(HttpLoggingFields.None);
    }

    private static HttpClient CreateClient(IConfiguration config) => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    { BaseAddress = Endpoint(config["LocalAudio:Endpoint"]), Timeout = TimeSpan.FromMinutes(3) };

    internal sealed record SpeechRequest(string? Text);
}
