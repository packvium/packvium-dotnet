using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Units;

namespace Packvium.Intelligence;

/// <summary>Base class for every error the Pareto report raises.</summary>
public class ParetoReportException(string message) : Exception(message);

/// <summary>
/// A metric axis the direction map names is missing from one side of a comparison -- it would
/// otherwise drop silently out of the dominance test.
/// </summary>
public sealed class InconsistentAxesException(string message) : ParetoReportException(message);

/// <summary>
/// A metric is NaN. Both <c>&gt;</c> and <c>&lt;</c> are false for NaN, so an unguarded axis would
/// count as equal and let garbage onto the frontier. Infinities are allowed on purpose: they are
/// the ends of the number line, and a caller encoding "unpriceable" as an infinite cost gets the
/// dominance answer they mean.
/// </summary>
public sealed class NonFiniteMetricException(string message) : ParetoReportException(message);

/// <summary>One engine's result on one profile, already validated by the caller.</summary>
public sealed record CandidateResult
{
    /// <summary>Refuses an empty profile, engine or metric set, and any NaN metric.</summary>
    public CandidateResult(string profile, string engine, IReadOnlyDictionary<string, double> metrics)
    {
        if (profile.Length == 0)
        {
            throw new ArgumentException("profile is required");
        }
        if (engine.Length == 0)
        {
            throw new ArgumentException("engine is required");
        }
        if (metrics.Count == 0)
        {
            throw new ArgumentException("metrics cannot be empty");
        }
        Profile = profile;
        Engine = engine;
        Metrics = MetricSet.Copy(metrics);
        // At construction, so an invalid candidate cannot exist and the report need not re-check it per pair.
        Pareto.RefuseNaN(Metrics, $"{engine} on profile {profile}");
    }

    /// <summary>The profile, or order, this result belongs to.</summary>
    public string Profile { get; }

    /// <summary>The engine, or arm, that produced it.</summary>
    public string Engine { get; }

    /// <summary>The metric axes, in code-point order of their names.</summary>
    public ImmutableSortedDictionary<string, double> Metrics { get; }

    /// <summary>Equal when every field is, with arrays and maps compared by content.</summary>
    public bool Equals(CandidateResult? other) =>
        other is not null && Profile == other.Profile && Engine == other.Engine && MetricSet.Same(Metrics, other.Metrics);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Profile, Engine, Metrics.Count);
}

/// <summary>
/// One profile's Pareto frontier. <see cref="Winner"/> is set only when exactly one candidate
/// survived as non-dominated; a genuine trade-off leaves it null and lists every surviving engine.
/// </summary>
public sealed record ProfileReport
{
    /// <summary>Refuses a winner off the frontier, or a winner beside a frontier of more than one.</summary>
    public ProfileReport(string profile, ImmutableArray<string> paretoOptimal, ImmutableArray<string> dominated,
        string? winner = null)
    {
        if (profile.Length == 0)
        {
            throw new ArgumentException("profile is required");
        }
        paretoOptimal = paretoOptimal.IsDefault ? [] : paretoOptimal;
        if (winner is not null && !paretoOptimal.Contains(winner, StringComparer.Ordinal))
        {
            throw new ArgumentException("winner, when set, must be one of the Pareto-optimal engines");
        }
        if (winner is not null && paretoOptimal.Length != 1)
        {
            throw new ArgumentException("winner can only be set when exactly one candidate is Pareto-optimal");
        }
        Profile = profile;
        ParetoOptimal = paretoOptimal;
        Dominated = dominated.IsDefault ? [] : dominated;
        Winner = winner;
    }

    /// <summary>The profile this report covers.</summary>
    public string Profile { get; }

    /// <summary>The engines nothing else dominates, in code-point order.</summary>
    public ImmutableArray<string> ParetoOptimal { get; }

    /// <summary>The engines some other candidate dominates, in code-point order.</summary>
    public ImmutableArray<string> Dominated { get; }

    /// <summary>The single non-dominated engine, or null when more than one survived.</summary>
    public string? Winner { get; }

    /// <summary>Equal when every field is, with arrays and maps compared by content.</summary>
    public bool Equals(ProfileReport? other) =>
        other is not null && Profile == other.Profile && Winner == other.Winner
        && ParetoOptimal.SequenceEqual(other.ParetoOptimal) && Dominated.SequenceEqual(other.Dominated);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Profile, Winner, ParetoOptimal.Length, Dominated.Length);
}

/// <summary>
/// A Pareto report over caller-supplied metrics (<c>packvium.pareto</c>). It never folds the axes
/// into one weighted number: a result dominates another only when it is no worse on every named
/// axis and strictly better on at least one, and the report per profile is the set nothing else
/// dominates, not a ranking.
/// </summary>
public static class Pareto
{
    /// <summary>
    /// Whether <paramref name="a"/> dominates <paramref name="b"/> on every axis
    /// <paramref name="higherIsBetter"/> names. Throws <see cref="InconsistentAxesException"/> when
    /// either side lacks a named axis and <see cref="NonFiniteMetricException"/> when either
    /// carries a NaN; an axis the map does not name is not compared. O(axes).
    /// </summary>
    public static bool Dominates(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b,
        IReadOnlyDictionary<string, bool> higherIsBetter)
    {
        var missing = higherIsBetter.Keys.Where(axis => !a.ContainsKey(axis) || !b.ContainsKey(axis))
            .Order(CodePointComparer.Instance).ToList();
        if (missing.Count > 0)
        {
            throw new InconsistentAxesException($"missing metric axis/axes: {PythonText.ReprList(missing)}");
        }
        // Public and handed bare maps, so it cannot rely on CandidateResult having checked them.
        RefuseNaN(a, "left candidate");
        RefuseNaN(b, "right candidate");
        var atLeastAsGood = true;
        var strictlyBetterSomewhere = false;
        foreach (var (axis, higherWins) in higherIsBetter)
        {
            var (left, right) = (a[axis], b[axis]);
            var better = higherWins ? left > right : left < right;
            var worse = higherWins ? left < right : left > right;
            atLeastAsGood &= !worse;
            strictlyBetterSomewhere |= better;
        }
        return atLeastAsGood && strictlyBetterSomewhere;
    }

    /// <summary>
    /// One report per distinct profile, sorted by profile name in code-point order. Every axis
    /// needs an explicit direction: guessing that runtime is higher-is-better would silently
    /// invert the report. O(p * c^2 * axes) for c candidates in a profile.
    /// </summary>
    public static ImmutableArray<ProfileReport> GenerateReport(IEnumerable<CandidateResult> candidates,
        IReadOnlyDictionary<string, bool> higherIsBetter)
    {
        if (higherIsBetter.Count == 0)
        {
            throw new ArgumentException("higher_is_better must name at least one metric axis");
        }
        var byProfile = new SortedDictionary<string, List<CandidateResult>>(CodePointComparer.Instance);
        foreach (var candidate in candidates)
        {
            if (!byProfile.TryGetValue(candidate.Profile, out var group))
            {
                byProfile[candidate.Profile] = group = [];
            }
            group.Add(candidate);
        }
        var reports = ImmutableArray.CreateBuilder<ProfileReport>(byProfile.Count);
        foreach (var (profile, group) in byProfile)
        {
            if (group.Select(candidate => candidate.Engine).Distinct(StringComparer.Ordinal).Count() != group.Count)
            {
                throw new ParetoReportException($"profile {PythonText.Repr(profile)} has more than one result for the same engine");
            }
            var (optimal, dominated) = Frontier(group, higherIsBetter);
            reports.Add(new ProfileReport(profile, optimal, dominated, optimal.Length == 1 ? optimal[0] : null));
        }
        return reports.MoveToImmutable();
    }

    /// <summary>
    /// Refuses a NaN metric, naming the candidate that carried it. Axes are checked in code-point
    /// order of their names, so the axis named is the same however the map was built.
    /// </summary>
    internal static void RefuseNaN(IReadOnlyDictionary<string, double> metrics, string where)
    {
        foreach (var axis in metrics.Keys.Order(CodePointComparer.Instance))
        {
            if (double.IsNaN(metrics[axis]))
            {
                throw new NonFiniteMetricException($"{where}: metric {PythonText.Repr(axis)} is NaN and cannot be compared");
            }
        }
    }

    private static (ImmutableArray<string> Optimal, ImmutableArray<string> Dominated) Frontier(
        List<CandidateResult> candidates, IReadOnlyDictionary<string, bool> higherIsBetter)
    {
        var optimal = new List<string>();
        var dominated = new List<string>();
        foreach (var candidate in candidates)
        {
            var isDominated = candidates.Any(other =>
                !ReferenceEquals(other, candidate) && Dominates(other.Metrics, candidate.Metrics, higherIsBetter));
            (isDominated ? dominated : optimal).Add(candidate.Engine);
        }
        return ([.. optimal.Order(CodePointComparer.Instance)], [.. dominated.Order(CodePointComparer.Instance)]);
    }
}

/// <summary>Metric maps as immutable values: sorted by name in code-point order, compared by content.</summary>
internal static class MetricSet
{
    public static ImmutableSortedDictionary<string, double> Copy(IReadOnlyDictionary<string, double> metrics) =>
        metrics.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, CodePointComparer.Instance);

    /// <summary>Python's mapping equality: the same names, and values equal under <c>==</c>.</summary>
    public static bool Same(IReadOnlyDictionary<string, double> left, IReadOnlyDictionary<string, double> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
