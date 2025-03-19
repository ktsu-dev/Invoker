// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Disabled for sample code", Scope = "member", Target = "~M:ktsu.Invoker.Sample.Main~System.Threading.Tasks.Task")]
[assembly: SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Disabled for sample code", Scope = "member", Target = "~M:ktsu.Invoker.Sample.Main~System.Threading.Tasks.Task")]
