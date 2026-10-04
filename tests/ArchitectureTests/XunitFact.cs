// SPDX-License-Identifier: AGPL-3.0-only

namespace Xunit;

// The shared policy engine identifies real contract tests by this stable attribute identity.
// The host is deliberately package-free (it adds no test framework dependency), runs its own suite and
// therefore declares the one attribute the engine recognises.
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class FactAttribute : Attribute { }
