// SPDX-License-Identifier: AGPL-3.0-only

namespace Xunit
{
    // The shared policy engine identifies real contract tests by this stable attribute identity.
    // The host is deliberately package-free (it adds no test framework dependency), runs its own suite and
    // therefore declares the one attribute the engine recognises.
    [AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public sealed class FactAttribute : Attribute { }
}

namespace ArcForges.ArcScope.ArchitectureTests
{
    internal enum PolicyGateStage
    {
        ValidateArguments,
        RunArchitectureFixtures,
        RunLocalArchitecture,
        LocateRepository,
        ValidateHostedIdentity,
        ValidateRp01Evidence,
        ValidateRp08Evidence,
        ValidateHostedWorkflow,
        ReadProjectGraph,
        ReadProjectCompilations,
        MissingSourceOrReferenceInputs,
        UnsupportedOutputType,
        ReconstructedCompilationDiagnostics,
        ValidateContractConsumption,
        ReadDependencyPolicy,
        ReadPolicyExceptions,
        BindContractTests,
        EvaluateSharedPolicy,
        ValidatePolicyResults,
    }

    internal static class Program
    {
        public static int Main(string[] args)
        {
            PolicyGateStage stage = PolicyGateStage.ValidateArguments;
            try
            {
                if (args.Length > 1 || (args.Length == 1 && args[0] != "--hosted"))
                {
                    throw new InvalidOperationException("Expected no arguments or --hosted.");
                }

                stage = PolicyGateStage.RunArchitectureFixtures;
                ArchitectureFixtureTests.Run();
                stage = PolicyGateStage.RunLocalArchitecture;
                ArcScopeArchitectureTests.RunLocal();
                if (args.Length == 1)
                {
                    HostedPolicyGate.Run(next => stage = next);
                    Console.WriteLine("ArcScope architecture policy passed for the exact hosted source revision.");
                }
                else
                {
                    Console.WriteLine("Local architecture fixtures passed; the hosted project graph, naming and secret-scan evidence remains unverified.");
                }

                return 0;
            }
            catch (Exception exception)
            {
                // Never echo environment variables or build-job secrets.
                WriteFailure(Console.Error, stage, exception);
                return 1;
            }
        }

        internal static void WriteFailure(TextWriter writer, PolicyGateStage stage, Exception exception)
        {
            _ = exception;
            writer.WriteLine($"ArcScope architecture policy failed closed at stage {stage}.");
        }
    }
}
