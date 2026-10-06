// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ArcForges.Application.Abstractions;
using ArcForges.Contracts.Foundation.Serialization;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.LocalRpc.Scope.V1;
using ArcForges.Foundation;
using ArcForges.Foundation.Execution;
using ArcForges.Native.Abstractions;
using ArcForges.Native.Image;

namespace ArcForges.ArcScope;

/// <summary>Explicit local proof of the immutable package consumer. Never invoked by startup or CI.</summary>
internal static unsafe class NativePackageProof
{
    public static int Run(string evidence)
    {
        if (Environment.GetEnvironmentVariable("CI") is not null || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null)
            return 2;
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["rid"] = RuntimeInformation.RuntimeIdentifier,
            ["nativeAot"] = !RuntimeFeature.IsDynamicCodeSupported,
            ["scope"] = "Published win-x64 Image ABI1.0 and Foundation primitives; no assistant, child-channel or functional ABI1.1 acceptance",
            ["success"] = false
        };
        try
        {
            // Managed service prerequisites may advance independently of the retained native ABI candidate.
            using var packageLock = typeof(Program).Assembly.GetManifestResourceStream("ArcScope.PackageLock")
                ?? throw new InvalidOperationException("The actual compiled package lock is missing.");
            var dependencies = JsonNode.Parse(packageLock)!["dependencies"]!.AsObject();
            string PackageVersion(string id) => dependencies
                .Select(framework => framework.Value![id]?["resolved"]?.GetValue<string>())
                .Where(version => version is not null).Distinct(StringComparer.Ordinal).Single()
                ?? throw new InvalidOperationException("The actual package version is missing: " + id);
            report["platformVersion"] = PackageVersion("ArcForges.Foundation");
            report["nativeImageVersion"] = PackageVersion("ArcForges.Native.Image");
            report["contractsVersion"] = PackageVersion("ArcForges.Contracts.LocalRpc.Scope");
            Require(!RuntimeFeature.IsDynamicCodeSupported, "Use the published Native AOT executable.");
            Require(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
                "The current immutable native producer supports win-x64 only.");
            var workspace = new WorkspaceId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
            Require(Convert.ToHexString(workspace.ToWire().Value.Span) == "00112233445566778899AABBCCDDEEFF", "UUID network-byte-order mismatch.");
            Require(WorkspaceId.FromWire(workspace.ToWire()) == workspace, "Generated identity round trip failed.");
            var instant = new Instant(-1, 999_999_999);
            Require(WireValues.ReadInstant(WireValues.ToWire(instant)) == instant, "Exact instant round trip failed.");
            Require(IdentityGeneration.NewCommand() != IdentityGeneration.NewCommand(), "Identity generation returned a duplicate.");
            IExecutionContext context = new ProofContext(
                new ExecutionIdentity(ExecutionOwner.ForTask(ArcForges.Foundation.Execution.TaskId.New()),
                    IdentityGeneration.NewCommand(), InvocationId.New(), RunId.New(), StepId.New(), AttemptId.New()),
                Clock.System, CancellationToken.None);
            Require(!context.Cancellation.IsCancellationRequested && context.Identity.Owner.Task is not null,
                "Published execution port did not preserve explicit owner/cancellation.");
            var scopeValue = new ScopeOperationsServiceCreateFindingValue
            {
                Revision = new ArcForges.Contracts.Foundation.V1.NativeContentRev { Value = ulong.MaxValue }
            };
            var scopeBytes = ContractWire.Encode(scopeValue, WireLimit.UnaryMessage);
            var decodedScope = ContractWire.Decode(ScopeOperationsServiceCreateFindingValue.Parser, scopeBytes, WireLimit.UnaryMessage);
            Require(decodedScope.Revision.Value == ulong.MaxValue, "Generated in-process Scope contract lost exact revision bits.");
            var version = ImageAbi.GetAbiVersion();
            Require(version.Major == 1 && version.Minor == 0, "Unexpected native ABI candidate.");
            var build = ImageAbi.GetBuildInfo(); // The published wrapper enforces both buffer-size phases and strict UTF-8.
            Require(build.StartsWith("ArcImageNative", StringComparison.Ordinal), "Unexpected native build identity.");
            // ImageAbi first validates the packaged native manifest and its dependencies; resolve the same absolute module.
            var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "ArcImageNative.dll"));
            try
            {
                var negotiate = (delegate* unmanaged[Cdecl]<uint*, uint*, int>)NativeLibrary.GetExport(library, "arc_image_get_abi_version");
                uint minor = 0;
                Require(negotiate(null, &minor) == (int)NativeStatus.InvalidArgument, "Null ABI output was accepted.");
                var error = ImageAbi.GetLastError();
                Require(error.Status == NativeStatus.InvalidArgument && error.Message.Length > 0, "Native thread-local error was not retained.");
                Require(ImageAbi.GetAbiVersion() == version, "Native ABI did not recover after invalid input.");
            }
            finally { NativeLibrary.Free(library); }
            report["abi"] = $"{version.Major}.{version.Minor}";
            report["buildInfo"] = build;
            report["success"] = true;
        }
        catch (Exception error) { report["error"] = error.ToString(); }
        var path = Path.GetFullPath(evidence);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToJsonString() + "\n");
        return report["success"]!.GetValue<bool>() ? 0 : 1;
    }

    private sealed record ProofContext(ExecutionIdentity Identity, IClock Clock, CancellationToken Cancellation) : IExecutionContext;

    private static void Require(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}
