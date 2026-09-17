// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArcForges.ArcScope;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? evidence = null;
        if (args.Length != 0)
        {
            if (args is not ["--smoke-live", "--evidence", var path]) return 2;
            evidence = Path.GetFullPath(path);
        }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<CloudHelloClient>();
        builder.Services.AddSingleton<IHelloClient>(services => services.GetRequiredService<CloudHelloClient>());
        builder.Services.AddTransient<HelloViewModel>();
        using var host = builder.Build();
        host.StartAsync().GetAwaiter().GetResult();
        var result = AppBuilder.Configure(() => new ArcScopeApp(host.Services, evidence))
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime([]);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        host.StopAsync(shutdown.Token).GetAwaiter().GetResult();
        return result;
    }
}
