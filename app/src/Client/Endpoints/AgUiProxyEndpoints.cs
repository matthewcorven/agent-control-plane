using System.Net;

namespace AGUIWebChat.Client.Endpoints;

public static class AgUiProxyEndpoints
{
    private static readonly string[] Paths =
    [
        "/ag-ui", "/ag-ui/tools", "/ag-ui/approval", "/ag-ui/shared-state",
        "/ag-ui/predictive-state", "/ag-ui/generative-ui", "/ag-ui/reasoning",
        "/ag-ui/harness-planning", "/ag-ui/harness-loop", "/ag-ui/harness-memory", "/ag-ui/harness-skills", "/ag-ui/harness-assistant"
    ];

    public static void AddAgUiProxy(this IServiceCollection services, string serverUrl)
    {
        var upstream = new Uri(serverUrl, UriKind.Absolute);
        if (upstream.Scheme is not ("http" or "https") || upstream.UserInfo.Length != 0)
            throw new InvalidOperationException("AGUI_SERVER_URL must be an HTTP(S) backend without credentials.");

        // A consumed decision must not inherit ServiceDefaults' retry/timeout pipeline.
#pragma warning disable EXTEXP0001
        services.AddHttpClient("agui-relay", client =>
        {
            client.BaseAddress = upstream;
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).RemoveAllResilienceHandlers().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            MaxResponseDrainSize = 0,
            AutomaticDecompression = DecompressionMethods.None
        });
#pragma warning restore EXTEXP0001
    }

    public static void MapAgUiProxy(this WebApplication app)
    {
        foreach (string path in Paths)
            app.MapPost(path, RelayAsync).DisableAntiforgery();
        app.Map("/ag-ui/{**path}", () => Results.Problem(statusCode: 404, title: "Unknown agent route."));
    }

    private static async Task RelayAsync(HttpContext context, IHttpClientFactory clients,
        ILoggerFactory loggerFactory)
    {
        if (!Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out var expected)
            || !Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            || origin.UserInfo.Length != 0 || origin.Port != expected.Port
            || !string.Equals(origin.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(origin.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
            || context.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
        {
            await Results.Problem(statusCode: 403, title: "Same-origin AG-UI request required.",
                extensions: new Dictionary<string, object?> { ["code"] = "relay_origin_rejected", ["restartRequired"] = false })
                .ExecuteAsync(context);
            return;
        }
        if (!context.Request.HasJsonContentType())
        {
            await Results.Problem(statusCode: 415, title: "AG-UI requests require application/json.",
                extensions: new Dictionary<string, object?> { ["code"] = "relay_content_type", ["restartRequired"] = false })
                .ExecuteAsync(context);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post,
            context.Request.Path.Value + context.Request.QueryString.Value)
        {
            Content = new StreamContent(context.Request.Body)
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        if (context.Request.Headers.TryGetValue("Cookie", out var cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie.ToArray());

        try
        {
            using var response = await clients.CreateClient("agui-relay").SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
                context.Response.ContentType = contentType.ToString();
            if (response.Content.Headers.ContentEncoding.Count != 0)
                context.Response.Headers.ContentEncoding = response.Content.Headers.ContentEncoding.ToArray();
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                foreach (string value in setCookies) context.Response.Headers.Append("Set-Cookie", value);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, context.RequestAborted)) != 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The browser owns cancellation; never retry a consumed approval.
        }
        catch (HttpRequestException error)
        {
            loggerFactory.CreateLogger("AgUiRelay").LogError(error, "AG-UI backend connection failed.");
            if (context.Response.HasStarted) context.Abort();
            else await Results.Problem(statusCode: 502, title: "The agent backend is unavailable.",
                extensions: new Dictionary<string, object?> { ["code"] = "relay_unavailable", ["restartRequired"] = false })
                .ExecuteAsync(context);
        }
    }
}
