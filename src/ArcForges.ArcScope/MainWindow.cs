// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace ArcForges.ArcScope;

internal sealed class MainWindow : Window
{
    private readonly HelloViewModel _state;
    private readonly TextBox _name = new() { PlaceholderText = "Your name", Name = "HelloName" };
    private readonly TextBlock _greeting = new() { FontSize = 27, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _count = new() { FontSize = 12, Foreground = Brushes.DimGray };
    private readonly Button _send = new() { Content = "Say hello", Name = "SayHello", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _cancel = new() { Content = "Cancel request", HorizontalAlignment = HorizontalAlignment.Stretch };
    public Task PendingAction { get; private set; } = Task.CompletedTask;
    public string? DisplayedGreeting => _greeting.Text;

    public MainWindow(HelloViewModel state)
    {
        _state = state;
        Title = "ArcScope";
        Width = 620;
        Height = 670;
        MinWidth = 360;
        MinHeight = 460;
        Background = Brush.Parse("#F5F6EF");
        var contents = new StackPanel { Spacing = 20, Margin = new Thickness(36), MaxWidth = 560 };
        contents.Children.Add(new TextBlock { Text = "ARCFORGES / ARCSCOPE", Foreground = Brush.Parse("#305E46"), FontWeight = FontWeight.Bold, LetterSpacing = 1 });
        contents.Children.Add(new TextBlock { Text = "A small beginning.", FontSize = 36, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        contents.Children.Add(new TextBlock { Text = "Your ideas, connected.", FontSize = 18 });
        var form = new StackPanel { Spacing = 14, Margin = new Thickness(26) };
        form.Children.Add(_greeting);
        form.Children.Add(new TextBlock { Text = "Your name" });
        form.Children.Add(_name);
        form.Children.Add(_count);
        form.Children.Add(_error);
        form.Children.Add(_send);
        form.Children.Add(_cancel);
        contents.Children.Add(new Border { Child = form, Background = Brushes.White, CornerRadius = new CornerRadius(20), Margin = new Thickness(0, 12, 0, 0) });
        contents.Children.Add(new TextBlock { Text = "Cloud Hello · arcforges.com", Foreground = Brushes.DimGray });
        Content = new ScrollViewer { Content = contents };
        _name.Text = state.Name;
        _name.TextChanged += (_, _) => state.Name = _name.Text ?? "";
        _send.Click += (_, _) => PendingAction = state.SendAsync();
        _cancel.Click += (_, _) => state.Cancel();
        state.PropertyChanged += OnStateChanged;
        Closed += (_, _) => state.PropertyChanged -= OnStateChanged;
        Refresh();
    }

    private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        _greeting.Text = _state.IsBusy ? "Connecting..." : _state.Greeting;
        _error.Text = _state.Error;
        _error.IsVisible = _state.Error is not null;
        _count.Text = $"{_state.Name.Length}/256";
        _name.IsEnabled = !_state.IsBusy;
        _send.IsEnabled = _state.CanSend;
        _cancel.IsVisible = _state.IsBusy;
        _send.Content = _state.IsBusy ? "Connecting..." : "Say hello";
    }

    internal async Task InvokeHelloAction(string name)
    {
        _name.Text = name;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        if (_state.Name != name) throw new InvalidOperationException("Text entry did not update the UI state.");
        if (!_send.IsEnabled) throw new InvalidOperationException("The native UI action is not enabled.");
        _send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await PendingAction;
    }
}
