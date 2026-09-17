// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Concurrent;
using ArcForges.ArcScope.Core;
using ArcForges.Contracts.Hello.V1;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class TransportTests
{
    [Theory]
    [InlineData("World")]
    [InlineData(" 世界 👋 ")]
    public async Task PublishedClientUsesBinaryGrpcWebAndApiPrefix(string name)
    {
        await using var server = await Fixture.Start();
        using var client = new CloudHelloClient(server.Endpoint, TimeSpan.FromSeconds(5));
        Assert.Equal($"Hello, {name}!", await client.SayHelloAsync(name, TestContext.Current.CancellationToken));
        var request = Assert.Single(server.Requests);
        Assert.Equal("/api/arcforges.hello.v1.HelloService/SayHello", request.Path);
        Assert.StartsWith("application/grpc-web", request.ContentType);
        Assert.DoesNotContain("text", request.ContentType);
        Assert.Equal("HTTP/1.1", request.Protocol);
    }

    [Theory]
    [InlineData(0, StatusCode.InvalidArgument)]
    [InlineData(257, StatusCode.ResourceExhausted)]
    public async Task ApplicationStatusIsPreserved(int length, StatusCode status)
    {
        await using var server = await Fixture.Start();
        using var client = new CloudHelloClient(server.Endpoint, TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<RpcException>(() => client.SayHelloAsync(new string('x', length), TestContext.Current.CancellationToken));
        Assert.Equal(status, failure.StatusCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task DeadlineAbortsPendingRequestWithoutAnApplicationRetry()
    {
        await using var server = await Fixture.Start(stall: true);
        using var client = new CloudHelloClient(server.Endpoint, TimeSpan.FromMilliseconds(500));
        var failure = await Assert.ThrowsAsync<RpcException>(() => client.SayHelloAsync("World", TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.DeadlineExceeded, failure.StatusCode);
        await server.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task CancellationReachesServer()
    {
        await using var server = await Fixture.Start(stall: true);
        using var client = new CloudHelloClient(server.Endpoint, TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var pending = client.SayHelloAsync("World", cancel.Token);
        await server.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        Assert.Equal(StatusCode.Cancelled, (await Assert.ThrowsAsync<RpcException>(() => pending)).StatusCode);
        await server.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task GatewayFailureDoesNotBecomeAFakeGreeting()
    {
        await using var server = await Fixture.Start(gatewayFailure: true);
        using var client = new CloudHelloClient(server.Endpoint, TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<RpcException>(() => client.SayHelloAsync("World", TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.Unavailable, failure.StatusCode);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("http://example.com/api")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/api/api")]
    [InlineData("https://example.com/api?token=value")]
    public void RejectsUnsafeOrIncorrectEndpoint(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new CloudHelloClient(new Uri(endpoint), TimeSpan.FromSeconds(5)));

    public sealed class Fixture : IAsyncDisposable
    {
        private WebApplication _app = null!;
        public Uri Endpoint { get; private set; } = null!;
        public ConcurrentQueue<(string Path, string ContentType, string Protocol)> Requests { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stall { get; private init; }

        public static async Task<Fixture> Start(bool stall = false, bool gatewayFailure = false)
        {
            var fixture = new Fixture { Stall = stall };
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(fixture);
            fixture._app = builder.Build();
            fixture._app.Use(async (context, next) =>
            {
                fixture.Requests.Enqueue((context.Request.Path, context.Request.ContentType ?? "", context.Request.Protocol));
                if (gatewayFailure) { context.Response.StatusCode = 503; return; }
                await next();
            });
            fixture._app.UsePathBase("/api");
            fixture._app.UseRouting();
            fixture._app.UseGrpcWeb();
            fixture._app.MapGrpcService<HelloEndpoint>().EnableGrpcWeb();
            await fixture._app.StartAsync(TestContext.Current.CancellationToken);
            var address = fixture._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            fixture.Endpoint = new Uri(address + "/api");
            return fixture;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    public sealed class HelloEndpoint(Fixture fixture) : HelloService.HelloServiceBase
    {
        public override async Task<SayHelloResponse> SayHello(SayHelloRequest request, ServerCallContext context)
        {
            fixture.Started.TrySetResult();
            if (fixture.Stall)
            {
                try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                finally { fixture.Aborted.TrySetResult(); }
            }
            if (request.Name.Length == 0) throw new RpcException(new Status(StatusCode.InvalidArgument, "name required"));
            if (request.Name.Length > 256) throw new RpcException(new Status(StatusCode.ResourceExhausted, "name too long"));
            return new SayHelloResponse { Message = $"Hello, {request.Name}!" };
        }
    }
}
