// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using ArcForges.Contracts.Hello.V1;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;

namespace ArcForges.ArcScope.Core;

public interface IHelloClient
{
    Task<string> SayHelloAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Own one channel per application host, never per button press.</summary>
public sealed class CloudHelloClient : IHelloClient, IDisposable
{
    public static readonly Uri Endpoint = new("https://arcforges.com/api");
    private readonly GrpcChannel _channel;
    private readonly HelloService.HelloServiceClient _client;
    private readonly TimeSpan _deadline;

    public CloudHelloClient() : this(Endpoint, TimeSpan.FromSeconds(5)) { }

    public CloudHelloClient(Uri endpoint, TimeSpan deadline)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.AbsolutePath != "/api" ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0 ||
            (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback)))
            throw new ArgumentException("Use HTTPS with exactly /api; HTTP is allowed only for loopback tests.", nameof(endpoint));
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(deadline));
        _deadline = deadline;
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var handler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new ApiPrefixHandler(sockets));
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _channel = GrpcChannel.ForAddress(endpoint.GetLeftPart(UriPartial.Authority), new GrpcChannelOptions
        {
            HttpClient = http,
            DisposeHttpClient = true,
            HttpVersion = HttpVersion.Version11,
            HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            MaxReceiveMessageSize = 64 * 1024,
            MaxSendMessageSize = 8 * 1024
        });
        _client = new HelloService.HelloServiceClient(_channel);
    }

    public async Task<string> SayHelloAsync(string name, CancellationToken cancellationToken)
    {
        using var call = _client.SayHelloAsync(new SayHelloRequest { Name = name },
            deadline: DateTime.UtcNow.Add(_deadline), cancellationToken: cancellationToken);
        return (await call.ResponseAsync.ConfigureAwait(false)).Message;
    }

    public void Dispose() => _channel.Dispose();

    // GrpcChannel intentionally discards base URI paths; append the Worker prefix explicitly.
    private sealed class ApiPrefixHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing gRPC URI.");
            if (uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                throw new InvalidOperationException("The API prefix must be applied exactly once.");
            request.RequestUri = new UriBuilder(uri) { Path = "/api" + uri.AbsolutePath }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
