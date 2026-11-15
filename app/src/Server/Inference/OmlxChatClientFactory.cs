using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AGUIWebChat.Server.Inference;

/// <summary>Creates unauthenticated OpenAI-compatible clients restricted to local oMLX.</summary>
public sealed class OmlxChatClientFactory
{
    private readonly IConfiguration _configuration;
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public OmlxChatClientFactory(IConfiguration configuration) : this(configuration, () => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    }) { }

    internal OmlxChatClientFactory(IConfiguration configuration, Func<HttpMessageHandler> handlerFactory)
    {
        _configuration = configuration;
        _handlerFactory = handlerFactory;
    }

    public IChatClient Create(string model, string? catalogueEndpoint = null)
    {
        Uri endpoint = ResolveEndpoint(catalogueEndpoint);
        var http = new HttpClient(new NoAuthenticationHandler { InnerHandler = _handlerFactory() });
        var client = new OpenAIClient(new ApiKeyCredential("local-no-auth"), new OpenAIClientOptions
        {
            Endpoint = endpoint,
            Transport = new HttpClientPipelineTransport(http)
        });
        return client.GetChatClient(model).AsIChatClient();
    }

    internal Uri ResolveEndpoint(string? catalogueEndpoint)
    {
        string candidate = catalogueEndpoint ?? _configuration["OMLX_ENDPOINT"] ?? "http://127.0.0.1:8001/v1";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out IPAddress? address) || !IPAddress.IsLoopback(address) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath.TrimEnd('/') != "/v1")
        {
            throw new InvalidOperationException("oMLX endpoint must be an HTTP loopback IP address with path /v1 and no credentials, query, or fragment.");
        }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    private sealed class NoAuthenticationHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = null;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
