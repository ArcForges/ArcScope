// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArcForges.ArcScope.Core;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Grpc.Core;

namespace ArcForges.ArcScope;

/// <summary>Explicit CLI validation of the real published UI executable; never runs at normal startup.</summary>
internal static class LiveSmoke
{
    public static async Task<int> RunAsync(MainWindow window, HelloViewModel state, CloudHelloClient client, string evidence)
    {
        var report = new SmokeReport
        {
            NativeAot = !RuntimeFeature.IsDynamicCodeSupported,
            Rid = RuntimeInformation.RuntimeIdentifier,
            SourceRevision = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "SourceRevision").Value ?? "unknown",
            Version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]
        };
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        try
        {
            Require(report.NativeAot, "Run the published Native AOT executable, not dotnet run.");
            Require(window.IsVisible && window.ClientSize.Width > 0, "The native desktop window did not open.");
            report.Cloud = await WaitForCloudAsync();
            await window.InvokeHelloAction("ArcScope");
            Require(state.Error is null, state.Error ?? "UI request failed.");
            Require(window.DisplayedGreeting == "Hello, ArcScope!", "Unexpected UI response.");
            report.UiGreeting = window.DisplayedGreeting;
            report.Checks.Add("native-ui-live-action");
            foreach (var name in new[] { " 世界 👋 ", new string('x', 256) })
                Require(await client.SayHelloAsync(name, CancellationToken.None) == $"Hello, {name}!", "Unexpected protobuf response.");
            report.Checks.Add("unicode-whitespace-boundary");
            foreach (var (name, status) in new[] { ("", StatusCode.InvalidArgument), (new string('x', 257), StatusCode.ResourceExhausted) })
            {
                try
                {
                    await client.SayHelloAsync(name, CancellationToken.None);
                    throw new InvalidOperationException($"Expected {status}.");
                }
                catch (RpcException error) when (error.StatusCode == status) { report.Checks.Add(status.ToString()); }
            }
            report.Success = true;
        }
        catch (Exception error)
        {
            report.Error = error.ToString();
        }
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
            bitmap.Render(window);
            bitmap.Save(Path.ChangeExtension(evidence, ".png"), PngBitmapEncoderOptions.Default);
        }
        catch (Exception error)
        {
            report.Success = false;
            report.Error += "\nScreenshot: " + error;
        }
        await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(report, SmokeJson.Default.SmokeReport));
        return report.Success ? 0 : 1;
    }

    private static async Task<CloudHealth> WaitForCloudAsync()
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var elapsed = Stopwatch.StartNew();
        Exception? last = null;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(60))
        {
            try
            {
                using var response = await http.GetAsync("https://arcforges.com/api/healthz", timeout.Token);
                response.EnsureSuccessStatusCode();
                var health = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(timeout.Token), SmokeJson.Default.CloudHealth)
                    ?? throw new InvalidOperationException("Missing Cloud health.");
                Require(health.Service == "arcforges-cloud" && health.NativeAot, "Cloud is not the Native AOT Hello service.");
                Require(health.Revision.Length == 40 && health.Revision.All(char.IsAsciiHexDigit), "Invalid Cloud revision.");
                Require(response.Headers.GetValues("x-arcforges-worker-revision").Single() == health.Revision, "Worker/container revision mismatch.");
                return health;
            }
            catch (HttpRequestException error) { last = error; }
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        }
        throw new InvalidOperationException("Cloud readiness failed; no Hello request was retried.", last);
    }

    private static void Require(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}

internal sealed class SmokeReport
{
    public bool Success { get; set; }
    public bool NativeAot { get; set; }
    public string Rid { get; set; } = "";
    public string Version { get; set; } = "";
    public string SourceRevision { get; set; } = "";
    public CloudHealth? Cloud { get; set; }
    public string? UiGreeting { get; set; }
    public List<string> Checks { get; set; } = [];
    public string? Error { get; set; }
}
internal sealed record CloudHealth(string Service, string Revision, bool NativeAot);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(SmokeReport))]
[JsonSerializable(typeof(CloudHealth))]
internal partial class SmokeJson : JsonSerializerContext;
