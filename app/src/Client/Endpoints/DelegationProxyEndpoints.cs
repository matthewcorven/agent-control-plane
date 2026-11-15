using System.Net;
using System.Net.Http.Headers;

namespace AGUIWebChat.Client.Endpoints;

internal static class DelegationProxyEndpoints
{
    private const int RequestLimit = 16 * 1024;
    private const int ResponseLimit = 512 * 1024;

    internal static void MapDelegationProxy(this WebApplication app, string serverUrl)
    {
        var upstream = new Uri(serverUrl, UriKind.Absolute);
        if (upstream.Scheme is not ("http" or "https") || upstream.UserInfo.Length != 0 || !upstream.IsLoopback)
            throw new InvalidOperationException("Delegation requires a loopback HTTP(S) backend.");
        string configuredOrigin = app.Configuration["Delegation:AllowedOrigin"] ?? "";
        var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        var client = new HttpClient(handler) { BaseAddress = upstream, Timeout = TimeSpan.FromSeconds(15) };
        app.Lifetime.ApplicationStopped.Register(client.Dispose);
        foreach (string operation in new[] { "start", "status", "cancel" })
            app.MapPost("/api/delegation/" + operation, context => Relay(context, client, configuredOrigin, operation)).DisableAntiforgery();
    }

    internal static void MapFileAgentProxy(this WebApplication app, string serverUrl)
    {
        var upstream = new Uri(serverUrl, UriKind.Absolute);
        if (upstream.Scheme is not ("http" or "https") || upstream.UserInfo.Length != 0 || !upstream.IsLoopback)
            throw new InvalidOperationException("File agent requires a loopback backend.");
        string origin = app.Configuration["Delegation:AllowedOrigin"] ?? "";
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = upstream, Timeout = TimeSpan.FromSeconds(125) };
        app.Lifetime.ApplicationStopped.Register(client.Dispose);
        app.MapPost("/api/file-agent/run", async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
            if (context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer)
                || !ValidOrigin(origin) || !string.Equals(context.Request.Headers.Origin.ToString(), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                || context.Request.Headers["Sec-Fetch-Site"] != "same-origin" || context.Request.Headers["X-File-Agent"] != "1"
                || context.Request.QueryString.HasValue) { context.Response.StatusCode = 403; return; }
            if (!context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
            try
            {
                byte[] body = await ReadBounded(context.Request.Body, 1024 * 1024, context.RequestAborted);
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/file-agent/run");
                request.Headers.TryAddWithoutValidation("Origin", origin.TrimEnd('/'));
                request.Headers.TryAddWithoutValidation("X-File-Agent", "1");
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                byte[] output = await ReadBounded(stream, 1024 * 1024, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = "application/json";
                await context.Response.Body.WriteAsync(output, context.RequestAborted);
            }
            catch (InvalidDataException) { context.Response.StatusCode = 413; }
            catch (HttpRequestException) { context.Response.StatusCode = 502; }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { context.Response.StatusCode = 504; }
        }).DisableAntiforgery();
    }

    private static bool ValidOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.IsLoopback && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && string.Equals(value.TrimEnd('/'), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private static async Task Relay(HttpContext context, HttpClient client, string origin, string operation)
    {
        if (context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } statusPages) statusPages.Enabled = false;
        context.Response.Headers.CacheControl = "no-store";
        if (!ValidOrigin(origin) || !string.Equals(context.Request.Headers.Origin.ToString(), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            || context.Request.Headers["Sec-Fetch-Site"] != "same-origin"
            || context.Request.Headers["X-Delegation-Request"] != "1"
            || context.Request.QueryString.HasValue)
        { context.Response.StatusCode = 403; return; }
        if (operation == "start" && !context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
        if (context.Request.ContentLength > RequestLimit) { context.Response.StatusCode = 413; return; }
        string authorization = context.Request.Headers.Authorization.ToString();
        if (operation != "start" && (authorization.Length != 71 || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            || !authorization.AsSpan(7).ToString().All(Uri.IsHexDigit))) { context.Response.StatusCode = 404; return; }
        try
        {
            byte[] body = await ReadBounded(context.Request.Body, RequestLimit, context.RequestAborted);
            if (operation != "start" && body.Length != 0) { context.Response.StatusCode = 400; return; }
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/delegation/" + operation);
            request.Headers.TryAddWithoutValidation("Origin", origin.TrimEnd('/'));
            request.Headers.TryAddWithoutValidation("X-Delegation-Request", "1");
            if (operation != "start") request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
            if (operation == "start") { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (response.Content.Headers.ContentLength > ResponseLimit) { context.Response.StatusCode = 502; return; }
            await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            byte[] bytes = await ReadBounded(stream, ResponseLimit, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
        catch (InvalidDataException) { context.Response.StatusCode = 413; }
        catch (HttpRequestException) { context.Response.StatusCode = 502; }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { context.Response.StatusCode = 504; }
    }
    private static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellation)) != 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
