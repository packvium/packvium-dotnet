using System;

namespace Packvium.Execution;

/// <summary>
/// The adapter was handed something it cannot describe. An exception rather than a status:
/// unlike an infeasible packing request this is never a well-formed question the model cannot
/// answer -- it is a malformed result, and a plan derived from one would be a document about
/// nothing.
/// </summary>
public sealed class ExecutionPlanException(string message) : ArgumentException(message);
