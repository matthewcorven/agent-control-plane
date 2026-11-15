using AGUIWebChat.Client.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class AgUiRelayTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesBodyPathStatusContentAndSeparateCookieDeletionWithoutRetainingAuthority(bool https)
    {
        string? cookie = null;
        string? body = null;
        string? path = null;
        await using var fixture = await RelayFixture.StartAsync(async context =>
        {
            cookie = context.Request.Headers.Cookie.ToString();
            path = context.Request.Path + context.Request.QueryString;
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            body = await reader.ReadToEndAsync(Token);
            context.Response.StatusCode = 409;
            context.Response.ContentType = "application/problem+json";
            context.Response.Headers.Append("Set-Cookie", "agui-caller=unit-caller; Path=/ag-ui; HttpOnly; Secure; SameSite=None");
            context.Response.Headers.Append("Set-Cookie", "old=; Path=/ag-ui; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT; HttpOnly");
            await context.Response.WriteAsync("""{"code":"approval_consumed","restartRequired":true}""", Token);
        }, https);
        using var first = fixture.Request("/ag-ui/tools?query=kept", """{"text":"café 🧪"}""");
        first.Headers.TryAddWithoutValidation("Cookie", "agui-caller=unit-caller");
        using var response = await fixture.Client.SendAsync(first, Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("/ag-ui/tools?query=kept", path);
        Assert.Equal("""{"text":"café 🧪"}""", body);
        Assert.Equal("agui-caller=unit-caller", cookie);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"code":"approval_consumed","restartRequired":true}""", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(new[]
        {
            "agui-caller=unit-caller; Path=/ag-ui; HttpOnly; Secure; SameSite=None",
            "old=; Path=/ag-ui; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT; HttpOnly"
        }, response.Headers.GetValues("Set-Cookie"));
        using var second = fixture.Request("/ag-ui/tools", "{}");
        using var other = await fixture.Client.SendAsync(second, Token);
        Assert.Equal("", cookie);
    }

    [Fact]
    public async Task HarnessMemoryRouteRelaysStreamingResponse()
    {
        string? upstreamPath = null;
        await using var fixture = await RelayFixture.StartAsync(async context =>
        {
            upstreamPath = context.Request.Path;
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: memory-result\n\n", Token);
        });
        using var request = fixture.Request("/ag-ui/harness-memory", "{}");
        using var response = await fixture.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/ag-ui/harness-memory", upstreamPath);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("data: memory-result\n\n", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task DefaultPortOriginIsNormalized()
    {
        await using var fixture = await RelayFixture.StartAsync(context => context.Response.WriteAsync("ok", Token));
        using var request = fixture.Request("/ag-ui/tools", "{}");
        request.Headers.Host = "localhost:80";
        request.Headers.Remove("Origin");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        using var response = await fixture.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("https://foreign.example", "application/json", 403)]
    [InlineData(null, "application/json", 403)]
    [InlineData("self", "text/plain", 415)]
    [InlineData("self", "application/json", 403)]
    public async Task ExplicitOriginAndJsonGuardsDoNotAdmitUpstream(string? origin, string contentType, int status)
    {
        int calls = 0;
        await using var fixture = await RelayFixture.StartAsync(context =>
        {
            Interlocked.Increment(ref calls);
            return context.Response.WriteAsync("must not execute", Token);
        });
        using var request = fixture.Request("/ag-ui/tools", "{}");
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Remove("Origin");
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin == "self" ? fixture.Origin : origin);
        if (origin == "self" && status == 403) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        using var response = await fixture.Client.SendAsync(request, Token);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, calls);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UntrustedForwardedProtoCannotMakeHttpLookLikeHttps()
    {
        await using var fixture = await RelayFixture.StartAsync(context => context.Response.WriteAsync("must not execute", Token));
        using var request = fixture.Request("/ag-ui/tools", "{}");
        request.Headers.Remove("Origin");
        request.Headers.TryAddWithoutValidation("Origin", fixture.Origin.Replace("http:", "https:"));
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        using var response = await fixture.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/ag-ui/unlisted", "POST", 404)]
    [InlineData("/ag-ui/tools", "GET", 404)]
    public async Task RouteAndMethodAreAllowlisted(string path, string method, int status)
    {
        int calls = 0;
        await using var fixture = await RelayFixture.StartAsync(context =>
        {
            Interlocked.Increment(ref calls);
            return context.Response.WriteAsync("must not execute", Token);
        });
        using var request = fixture.Request(path, "{}");
        request.Method = new HttpMethod(method);
        using var response = await fixture.Client.SendAsync(request, Token);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(503)]
    public async Task DecisionsNeverFollowRedirectsOrRetryUpstream(int status)
    {
        int calls = 0;
        await using var fixture = await RelayFixture.StartAsync(context =>
        {
            Interlocked.Increment(ref calls);
            context.Response.StatusCode = status;
            context.Response.Headers.Location = "/ag-ui/reasoning";
            return context.Response.WriteAsync("original", Token);
        });
        using var request = fixture.Request("/ag-ui/tools", "{}");
        using var response = await fixture.Client.SendAsync(request, Token);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(1, calls);
        Assert.Equal("original", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task ProgressiveStreamingAndRelayCancellationDoNotWaitForUpstreamRelease()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await RelayFixture.StartAsync(async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try { await blocked.Task.WaitAsync(context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        using var socket = new TcpClient();
        socket.Client.LingerState = new LingerOption(true, 0);
        await socket.ConnectAsync(IPAddress.Loopback, fixture.Client.BaseAddress!.Port, Token);
        var stream = socket.GetStream();
        byte[] request = Encoding.ASCII.GetBytes($"POST /ag-ui/tools HTTP/1.1\r\nHost: {fixture.Client.BaseAddress.Authority}\r\n" +
            $"Origin: {fixture.Origin}\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{{}}");
        await stream.WriteAsync(request, Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        do { line = await reader.ReadLineAsync(Token); } while (line is not null && line != "data: first");
        Assert.Equal("data: first", line);
        Assert.False(blocked.Task.IsCompleted);
        // A browser abort resets the socket; graceful HTTP draining is not cancellation.
        socket.Dispose();
        await fixture.DownstreamCanceled.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.DownstreamCompleted.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(blocked.Task.IsCompleted);
        blocked.TrySetResult();
    }

    private sealed class RelayFixture(WebApplication upstream, WebApplication relay, HttpClient client, X509Certificate2? certificate,
        Task downstreamCanceled, Task downstreamCompleted) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public Task DownstreamCanceled { get; } = downstreamCanceled;
        public Task DownstreamCompleted { get; } = downstreamCompleted;
        public string Origin => Client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        public HttpRequestMessage Request(string path, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Origin", Origin);
            return request;
        }
        public static async Task<RelayFixture> StartAsync(RequestDelegate endpoint, bool https = false)
        {
            var backend = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            backend.Logging.ClearProviders();
            backend.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var upstream = backend.Build();
            upstream.MapPost("/ag-ui/{**path}", endpoint);
            await upstream.StartAsync(Token);
            X509Certificate2? certificate = null;
            if (https)
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder();
                names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
            {
                if (certificate is not null) listen.UseHttps(certificate);
            }));
            builder.Services.AddAgUiProxy(Address(upstream));
            var relay = builder.Build();
            var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            relay.Use(async (context, next) =>
            {
                using var registration = context.RequestAborted.Register(() => aborted.TrySetResult());
                try { await next(context); }
                finally { completed.TrySetResult(); }
            });
            relay.MapAgUiProxy();
            await relay.StartAsync(Token);
            var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, MaxResponseDrainSize = 0 };
            if (certificate is not null)
                handler.SslOptions.RemoteCertificateValidationCallback = (_, received, _, _) =>
                    received is not null && received.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData);
            return new RelayFixture(upstream, relay, new HttpClient(handler) { BaseAddress = new Uri(Address(relay)) },
                certificate, aborted.Task, completed.Task);
        }
        private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await relay.DisposeAsync();
            await upstream.DisposeAsync();
            certificate?.Dispose();
        }
    }
}
