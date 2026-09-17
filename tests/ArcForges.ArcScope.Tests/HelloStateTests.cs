// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core;
using Grpc.Core;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class HelloStateTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void InputBoundsAreVisibleBeforeSending(int length, bool allowed)
    {
        using var state = new HelloViewModel(new Stub((_, _) => throw new InvalidOperationException("No startup traffic")));
        state.Name = new string('x', length);
        Assert.Equal(allowed, state.CanSend);
        Assert.False(state.IsBusy);
    }

    [Fact]
    public async Task OnePendingActionPreservesInputAndPreventsDuplicates()
    {
        var completion = new TaskCompletionSource<string>();
        var names = new List<string>();
        using var state = new HelloViewModel(new Stub((name, _) => { names.Add(name); return completion.Task; }));
        state.Name = " 世界 👋 ";
        var pending = state.SendAsync();
        Assert.True(state.IsBusy);
        Assert.False(state.CanSend);
        state.Name = "cannot change pending input";
        await state.SendAsync();
        Assert.Equal(" 世界 👋 ", Assert.Single(names));
        completion.SetResult("Hello, 世界!");
        await pending;
        Assert.Equal("Hello, 世界!", state.Greeting);
        Assert.True(state.CanSend);
    }

    [Fact]
    public async Task FailureNeedsExplicitRetryAndDoesNotInventAResult()
    {
        var calls = 0;
        using var state = new HelloViewModel(new Stub((_, _) => ++calls == 1
            ? Task.FromException<string>(new RpcException(new Status(StatusCode.Unavailable, "private diagnostics")))
            : Task.FromResult("Hello, World!")));
        await state.SendAsync();
        Assert.Equal(1, calls);
        Assert.Equal("Ready to connect.", state.Greeting);
        Assert.Contains("Check your connection", state.Error);
        Assert.DoesNotContain("private diagnostics", state.Error);
        await state.SendAsync();
        Assert.Equal(2, calls);
        Assert.Null(state.Error);
        Assert.Equal("Hello, World!", state.Greeting);
    }

    [Fact]
    public async Task CancelReleasesBusyStateAndAllowsAnotherAction()
    {
        using var state = new HelloViewModel(new Stub(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "must not be shown";
        }));
        var pending = state.SendAsync();
        state.Cancel();
        await pending;
        Assert.False(state.IsBusy);
        Assert.True(state.CanSend);
        Assert.Contains("canceled", state.Error);
    }

    [Fact]
    public async Task ClosingCancelsAndDiscardsALateReply()
    {
        CancellationToken captured = default;
        var completion = new TaskCompletionSource<string>();
        using var state = new HelloViewModel(new Stub((_, token) => { captured = token; return completion.Task; }));
        var pending = state.SendAsync();
        state.Dispose();
        Assert.True(captured.IsCancellationRequested);
        completion.SetResult("late response");
        await pending;
        Assert.Equal("Ready to connect.", state.Greeting);
        Assert.False(state.CanSend);
    }

    private sealed class Stub(Func<string, CancellationToken, Task<string>> send) : IHelloClient
    {
        public Task<string> SayHelloAsync(string name, CancellationToken cancellationToken) => send(name, cancellationToken);
    }
}
