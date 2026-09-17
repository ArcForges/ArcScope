// SPDX-License-Identifier: AGPL-3.0-only
using System.ComponentModel;
using Grpc.Core;

namespace ArcForges.ArcScope.Core;

/// <summary>UI-owned state. The application host owns the client and its connection.</summary>
public sealed class HelloViewModel(IHelloClient client) : INotifyPropertyChanged, IDisposable
{
    private string _name = "World";
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name
    {
        get => _name;
        set
        {
            if (IsBusy || _disposed || _name == value) return;
            _name = value;
            Notify();
        }
    }

    public string Greeting { get; private set; } = "Ready to connect.";
    public string? Error { get; private set; }
    public bool IsBusy => _pending is not null;
    public bool CanSend => !_disposed && !IsBusy && Name.Length is >= 1 and <= 256;

    public async Task SendAsync()
    {
        if (!CanSend) return;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        Error = null;
        Notify();
        try
        {
            var greeting = await client.SayHelloAsync(Name, pending.Token);
            if (!_disposed) Greeting = greeting;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) Error = pending.IsCancellationRequested
                ? "The request was canceled. You can try again."
                : "Cloud did not respond in time. Try again.";
        }
        catch (RpcException failure)
        {
            if (!_disposed) Error = failure.StatusCode switch
            {
                StatusCode.InvalidArgument => "Cloud rejected this name. Check it and try again.",
                StatusCode.ResourceExhausted => "Cloud's request limit was reached. Try again later.",
                StatusCode.DeadlineExceeded => "Cloud did not respond in time. Try again.",
                StatusCode.Cancelled => "The request was canceled. You can try again.",
                _ => "Could not reach Cloud. Check your connection and try again."
            };
        }
        catch (HttpRequestException)
        {
            if (!_disposed) Error = "Could not reach Cloud. Check your connection and try again.";
        }
        catch (Exception)
        {
            if (!_disposed) Error = "Could not complete the request. Please try again.";
        }
        finally
        {
            _pending = null;
            if (!_disposed) Notify();
        }
    }

    public void Cancel() => _pending?.Cancel();
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }
}
