using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ProtocolWorkflows;

public static class ProtocolEndpoints
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public sealed record Request(string? Mode, string? Message, int Left, int Right);

    public static void MapLocalProtocolWorkflows(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        var configured = app.Configuration["ProtocolWorkflows:AllowedOrigin"];
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var origin) || !origin.IsLoopback ||
            origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return;
        var allowedOrigin = origin.GetLeftPart(UriPartial.Authority);
        app.MapPost("/api/local-protocols", async (HttpContext context) =>
        {
            if (context.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress) ||
                context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1") ||
                context.Request.Headers.Origin.Count != 1 || context.Request.Headers.Origin.ToString() != allowedOrigin ||
                context.Request.Headers["X-Protocol-Workflow"] != "1") return Results.StatusCode(403);
            if (context.Request.ContentLength > 4096 || !context.Request.HasJsonContentType()) return Results.BadRequest();
            Request? input;
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var body = new MemoryStream();
                var buffer = new byte[1024];
                int count;
                while ((count = await context.Request.Body.ReadAsync(buffer, readTimeout.Token)) > 0)
                {
                    if (body.Length + count > 4096) return Results.BadRequest();
                    body.Write(buffer, 0, count);
                }
                body.Position = 0;
                input = await System.Text.Json.JsonSerializer.DeserializeAsync<Request>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web), context.RequestAborted);
            }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return Results.StatusCode(408); }
            if (input is null || input.Mode is not ("stream" or "background" or "mcp") ||
                (input.Mode != "mcp" && (string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 1000))) return Results.BadRequest();
            if (!await Gate.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
            try
            {
                var result = input.Mode == "mcp" ? await LocalProtocols.McpAsync(input.Left, input.Right, context.RequestAborted) :
                    await LocalProtocols.A2AAsync(input.Message!, input.Mode, context.RequestAborted);
                return Results.Ok(result);
            }
            catch (Exception) { return Results.Problem("Local protocol operation failed.", statusCode: 500); }
            finally { Gate.Release(); }
        });
    }
}
