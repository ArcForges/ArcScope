// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace ArcForges.ArcScope;

internal sealed class ArcScopeApp(IServiceProvider services, string? evidence) : Application
{
    public override void Initialize()
    {
        Name = "ArcScope";
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var state = services.GetRequiredService<HelloViewModel>();
            var window = new MainWindow(state);
            desktop.MainWindow = window;
            window.Closed += (_, _) => state.Dispose();
            if (evidence is not null)
                window.Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
                    desktop.Shutdown(await LiveSmoke.RunAsync(window, state,
                        services.GetRequiredService<CloudHelloClient>(), evidence)));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
