using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Algorithms;

/// <summary>
/// A candidate's rank, lower first: floor before height, then the stack it would leave, then
/// the residual corner volume, then y and x.
/// </summary>
public readonly record struct CandidateScore(long Z, long NewHeight, Int128 Residual, long Y, long X)
    : IComparable<CandidateScore>
{
    public int CompareTo(CandidateScore other)
    {
        var order = Z.CompareTo(other.Z);
        if (order == 0) order = NewHeight.CompareTo(other.NewHeight);
        if (order == 0) order = Residual.CompareTo(other.Residual);
        if (order == 0) order = Y.CompareTo(other.Y);
        return order != 0 ? order : X.CompareTo(other.X);
    }

    public static bool operator <(CandidateScore left, CandidateScore right) => left.CompareTo(right) < 0;

    public static bool operator >(CandidateScore left, CandidateScore right) => left.CompareTo(right) > 0;

    public static bool operator <=(CandidateScore left, CandidateScore right) => left.CompareTo(right) <= 0;

    public static bool operator >=(CandidateScore left, CandidateScore right) => left.CompareTo(right) >= 0;
}

/// <summary>A feasible position for one item: envelope origin, physical position, rotation and rank.</summary>
public sealed record Candidate(
    Point Point,
    Point Position,
    Rotation Rotation,
    Dimensions Dimensions,
    Dimensions EnvelopeDimensions,
    CandidateScore Score);

/// <summary>
/// The shared feasibility engine every solver places through. One call is
/// O(p * r * (q + C + log k)) for p candidate points (capped by <c>max_candidate_points</c>), r
/// unique rotations, q spatial-index hits, C the constraint cost and k the candidate cap; a
/// bounded call keeps O(k) candidates, an unbounded one all f feasible ones and sorts them,
/// O(f log f).
/// </summary>
public static class CandidateFinder
{
    private sealed record Form(Rotation Rotation, Dimensions Physical, Dimensions Envelope, HullShape? Shape)
    {
        public long Dx => Envelope.Length.Ticks;

        public long Dy => Envelope.Width.Ticks;

        public long Dz => Envelope.Height.Ticks;
    }

    public static List<Candidate> Find(ContainerState state, ItemInstance item, PackingConfig config,
        IReadOnlyList<IPlacementConstraint> constraints, SearchStats stats, Deadline deadline, long? maxCandidates = null,
        IReadOnlyList<Point>? points = null)
    {
        var container = state.Container;
        if (container.MaxItems is { } maxItems && state.Placements.Count >= maxItems)
        {
            return [];
        }
        if (container.MaxPayload is { } payload && state.PayloadTicks + item.Weight.Ticks > payload.Ticks)
        {
            return [];
        }
        var compressionSensitive = state.CompressionSensitive || item.Item.ShapeType == ShapeType.Compressible;
        var reserveNeedsCandidate = item.Item.NestingHeight is not null || item.Item.ShapeType == ShapeType.ConvexHull
                                    || compressionSensitive;
        if (container.VoidFillReserveRatio > 0 && !reserveNeedsCandidate
            && state.UsedVolumeTicks + item.Dimensions.Volume > SupportRatio.UsableVolume(container))
        {
            return [];
        }
        var inner = container.InnerDimensions;
        var (limitX, limitY, limitZ) = (inner.Length.Ticks, inner.Width.Ticks, inner.Height.Ticks);
        var clearance = config.Clearance.Ticks;
        var forms = Forms(item, config, clearance);
        var placed = state.Placements.ToImmutableArray();
        var stackSensitive = state.StackSensitive || item.Item.IsStackSensitive || container.MaxStackDensity is not null;
        var routeSensitive = item.Item.StopIndex is not null || state.RouteSensitive;
        var nesting = item.Item.NestingHeight is not null;
        var reserveCheck = container.VoidFillReserveRatio > 0 && reserveNeedsCandidate;
        Int128 usable = reserveCheck ? SupportRatio.UsableVolume(container) : 0;
        var placementOffset = state.Bounds.Count - placed.Length;
        // Everything that decides whether a rule can fire is fixed for this call, so the chain
        // is pruned once here rather than answered "allow" once per position. The support rule
        // stays in regardless: SupportChecks counts every time the chain reaches it.
        var active = ConstraintActivity.Active(constraints, container, item, stackSensitive, routeSensitive)
            .Select(constraint => (constraint, constraint is SupportConstraint)).ToList();
        var ordered = points is null
            ? DefaultPoints(state, item, config, forms, limitX)
            : [.. points.OrderBy(p => (p.Z, p.Y, p.X))];
        var candidates = new List<Candidate>();
        Candidate? best = null;
        var bounded = maxCandidates > 1;
        // Root is the worst retained entry: the highest score, and on a tie the latest one.
        var retained = bounded ? new PriorityQueue<Candidate, (CandidateScore, long)>(Worst.Instance) : null;
        foreach (var point in ordered)
        {
            deadline.Check();
            stats.CandidatePointsConsidered++;
            foreach (var form in forms)
            {
                stats.PlacementsAttempted++;
                var (x1, y1, z1) = (point.X, point.Y, point.Z);
                var (x2, y2, z2) = (x1 + form.Dx, y1 + form.Dy, z1 + form.Dz);
                if (x2 > limitX || y2 > limitY || z2 > limitZ)
                {
                    continue;
                }
                Placement? tentative = !nesting
                    ? null
                    : new Placement(item, new Point(x1 + clearance, y1 + clearance, z1 + clearance), form.Rotation,
                        form.Physical, point, form.Envelope);
                if (Blocked(state, placed, placementOffset, form, tentative, point, stats))
                {
                    continue;
                }
                var context = new ConstraintContext(container, placed, item, point, form.Rotation, form.Physical,
                    form.Envelope, stackSensitive, routeSensitive);
                if (Rejected(active, context, stats))
                {
                    continue;
                }
                var score = Score(state, point, form.Envelope);
                var position = tentative?.Position ?? new Point(x1 + clearance, y1 + clearance, z1 + clearance);
                if (reserveCheck
                    && ProjectedVolume(state, placed, item, point, position, form, tentative, compressionSensitive, usable) > usable)
                {
                    continue;
                }
                stats.CandidatesEvaluated++;
                // Every check and counter above still runs; only a candidate that survives
                // selection is materialized. Equal scores keep the earlier enumeration entry.
                if (maxCandidates == 1)
                {
                    if (best is null || score < best.Score)
                    {
                        best = new Candidate(point, position, form.Rotation, form.Physical, form.Envelope, score);
                    }
                    continue;
                }
                if (retained is null)
                {
                    candidates.Add(new Candidate(point, position, form.Rotation, form.Physical, form.Envelope, score));
                    continue;
                }
                var key = (score, stats.CandidatesEvaluated);
                if (retained.Count < maxCandidates)
                {
                    retained.Enqueue(new Candidate(point, position, form.Rotation, form.Physical, form.Envelope, score), key);
                }
                else if (retained.TryPeek(out _, out var worst) && score < worst.Item1)
                {
                    retained.DequeueEnqueue(new Candidate(point, position, form.Rotation, form.Physical, form.Envelope, score), key);
                }
            }
        }
        if (maxCandidates == 1)
        {
            return best is null ? [] : [best];
        }
        if (retained is not null)
        {
            return [.. retained.UnorderedItems.OrderBy(entry => entry.Priority, Worst.Instance.Reversed).Select(entry => entry.Element)];
        }
        if (maxCandidates is not null)
        {
            return [];
        }
        // A stable sort keeps enumeration order as the deterministic tie-break.
        return [.. candidates.OrderBy(candidate => candidate.Score)];
    }

    /// <summary>
    /// Orders (score, enumeration ordinal) worst first, so a bounded queue's root is the entry the
    /// next better candidate replaces. Ordinals are unique, so the order is total and the
    /// selection does not depend on the heap's internal layout.
    /// </summary>
    private sealed class Worst : IComparer<(CandidateScore Score, long Ordinal)>
    {
        public static Worst Instance { get; } = new();

        public IComparer<(CandidateScore Score, long Ordinal)> Reversed { get; } =
            Comparer<(CandidateScore Score, long Ordinal)>.Create((x, y) => Instance.Compare(y, x));

        public int Compare((CandidateScore Score, long Ordinal) x, (CandidateScore Score, long Ordinal) y)
        {
            var order = y.Score.CompareTo(x.Score);
            return order != 0 ? order : y.Ordinal.CompareTo(x.Ordinal);
        }
    }

    /// <summary>The share of the base the direct supporters carry, as the nearest double.</summary>
    public static double SupportRatioOf(ContainerState state, ItemInstance item, Candidate candidate)
    {
        if (candidate.Point.Z == 0)
        {
            return 1.0;
        }
        var box = new AxisAlignedBox(candidate.Point, candidate.EnvelopeDimensions);
        var support = DirectSupportView.For(state.Placements, item, box);
        return DecimalRendering.TrueDivide(support.SupportingArea, candidate.EnvelopeDimensions.BaseArea);
    }

    public static ContainerState Extended(ContainerState state, ItemInstance item, Candidate candidate)
    {
        var child = state.Copy();
        child.Add(new Placement(item, candidate.Position, candidate.Rotation, candidate.Dimensions, candidate.Point,
            candidate.EnvelopeDimensions, SupportRatioOf(state, item, candidate)));
        return child;
    }

    /// <summary>
    /// Items in a group are offered atomically, in first-member order; an impossible group strands
    /// nothing else. One pass with a group-to-slot map: O(n) instead of a rescan per group. A
    /// batch's slot is its first member's, and members keep input order.
    /// </summary>
    public static List<ImmutableArray<ItemInstance>> GroupBatches(IReadOnlyList<ItemInstance> items)
    {
        var slots = new List<(ItemInstance? Single, ImmutableArray<ItemInstance>.Builder? Members)>();
        var groups = new Dictionary<string, ImmutableArray<ItemInstance>.Builder>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Item.Group is not { } group)
            {
                slots.Add((item, null));
                continue;
            }
            if (!groups.TryGetValue(group, out var members))
            {
                groups[group] = members = ImmutableArray.CreateBuilder<ItemInstance>();
                slots.Add((null, members));
            }
            members.Add(item);
        }
        return [.. slots.Select(slot => slot.Members is { } members ? members.ToImmutable() : [slot.Single!])];
    }

    /// <summary>States reachable by placing every member of a batch, best first, built lazily.</summary>
    public static IEnumerable<ContainerState> PlaceBatch(ContainerState state, ImmutableArray<ItemInstance> batch,
        PackingConfig config, IReadOnlyList<IPlacementConstraint> constraints, SearchStats stats, Deadline deadline,
        long? width)
    {
        if (batch.Length == 1)
        {
            foreach (var candidate in Find(state, batch[0], config, constraints, stats, deadline, width))
            {
                yield return Extended(state, batch[0], candidate);
            }
            yield break;
        }
        var working = state;
        foreach (var item in batch)
        {
            var found = Find(working, item, config, constraints, stats, deadline, 1);
            if (found.Count == 0)
            {
                yield break;
            }
            working = Extended(working, item, found[0]);
        }
        yield return working;
    }

    private static List<Form> Forms(ItemInstance item, PackingConfig config, long clearance)
    {
        // A hull is not the same solid under two rotations that give the same box, so hulls
        // keep every orientation; cuboids keep the box deduplication.
        var isHull = item.Item.ShapeType == ShapeType.ConvexHull;
        var exactHull = item.Item.HullCollisionIsExact(clearance == 0);
        var rotationForms = isHull
            ? item.Item.AllowedRotations.Select(rotation => (rotation, item.Dimensions.Rotated(rotation)))
            : item.Dimensions.UniqueRotations(item.Item.AllowedRotations).Select(pair => (pair.Rotation, pair.Dimensions));
        return [.. rotationForms.Select(pair =>
        {
            var envelope = clearance != 0 ? pair.Item2.Expand(config.Clearance) : pair.Item2;
            var shape = exactHull && item.Item.HullVertices is { } vertices ? Hull.ShapeFor(vertices, pair.Item1) : null;
            return new Form(pair.Item1, pair.Item2, envelope, shape);
        })];
    }

    private static List<Point> DefaultPoints(ContainerState state, ItemInstance item, PackingConfig config,
        List<Form> forms, long limitX)
    {
        var cap = (int)Math.Min(config.MaxCandidatePoints, int.MaxValue);
        List<Point> ordered;
        if (item.Item.NestingHeight is null)
        {
            ordered = [.. state.OrderedPoints.Take(cap)];
        }
        else
        {
            var merged = new Dictionary<(long, long, long), Point>();
            foreach (var point in state.OrderedPoints.Concat(NestingPoints(state, item)))
            {
                merged[(point.X, point.Y, point.Z)] = point;
            }
            ordered = [.. merged.Values.OrderBy(p => (p.Z, p.Y, p.X)).Take(cap)];
        }
        if (state.Container.Axles is { } axles)
        {
            ordered.AddRange(AxleBalancedPoints(state, item, forms, limitX, axles));
        }
        return ordered;
    }

    /// <summary>Origins where a nesting item sinks into an existing identical footprint, sorted (z, y, x).</summary>
    private static IEnumerable<Point> NestingPoints(ContainerState state, ItemInstance item)
    {
        if (item.Item.NestingHeight is not { } depth)
        {
            return [];
        }
        return state.Placements
            .Where(placement => placement.Instance.Item.Id == item.Item.Id)
            .Select(placement => (placement.EnvelopeOrigin.X, placement.EnvelopeOrigin.Y, Z: placement.EnvelopeBox.Z2 - depth.Ticks))
            .Distinct()
            .OrderBy(point => (point.Z, point.Y, point.X))
            .Where(point => point.Z >= 0)
            .Select(point => new Point(point.X, point.Y, point.Z));
    }

    /// <summary>Floor-level candidates that seat the item on an axle's own limit.</summary>
    private static List<Point> AxleBalancedPoints(ContainerState state, ItemInstance item, List<Form> forms,
        long limitX, (Axle Front, Axle Rear) axles)
    {
        var container = state.Container;
        var others = LoadUnit.From(state.Placements);
        var floorYs = state.Points.Where(point => point.Z == 0).Select(point => point.Y).Distinct().Order().ToList();
        if (floorYs.Count == 0)
        {
            floorYs.Add(0);
        }
        var points = new List<Point>();
        foreach (var form in forms)
        {
            foreach (var x1 in AxleLoad.BalancedOrigins(axles, others, container.TareWeight.Ticks,
                         container.InnerDimensions.Length.Ticks, item.Weight.Ticks, form.Dx))
            {
                if (0 <= x1 && x1 <= limitX - form.Dx)
                {
                    points.AddRange(floorYs.Select(y => new Point(x1, y, 0)));
                }
            }
        }
        return points;
    }

    private static bool Blocked(ContainerState state, ImmutableArray<Placement> placed, int placementOffset,
        Form form, Placement? tentative, Point point, SearchStats stats)
    {
        var (x1, y1, z1) = (point.X, point.Y, point.Z);
        var (x2, y2, z2) = (x1 + form.Dx, y1 + form.Dy, z1 + form.Dz);
        foreach (var index in state.Index.Query(x1, y1, z1, x2, y2, z2))
        {
            var b = state.Bounds[index];
            stats.CollisionChecks++;
            if (!(x1 < b.X2 && b.X1 < x2 && y1 < b.Y2 && b.Y1 < y2 && z1 < b.Z2 && b.Z1 < z2))
            {
                continue;
            }
            var placementIndex = index - placementOffset;
            if (tentative is not null && placementIndex >= 0 && Nesting.IsValidNesting(placed[placementIndex], tentative))
            {
                continue;
            }
            var blocker = state.HullShapes[index];
            if ((form.Shape is not null || blocker is not null)
                && !Hull.Collide(form.Shape ?? HullShape.Box(form.Dx, form.Dy, form.Dz), point.AsVertex,
                    blocker ?? HullShape.Box(b.X2 - b.X1, b.Y2 - b.Y1, b.Z2 - b.Z1), new Vertex(b.X1, b.Y1, b.Z1)))
            {
                stats.HullRefinements++;
                continue;
            }
            return true;
        }
        return false;
    }

    private static bool Rejected(List<(IPlacementConstraint Constraint, bool CountsSupport)> active,
        ConstraintContext context, SearchStats stats)
    {
        foreach (var (constraint, countsSupport) in active)
        {
            if (countsSupport)
            {
                stats.SupportChecks++;
            }
            if (!constraint.Evaluate(context).Allowed)
            {
                return true;
            }
        }
        return false;
    }

    private static Int128 ProjectedVolume(ContainerState state, ImmutableArray<Placement> placed, ItemInstance item,
        Point point, Point position, Form form, Placement? tentative, bool compressionSensitive, Int128 usable)
    {
        var reserve = tentative ?? new Placement(item, position, form.Rotation, form.Physical, point, form.Envelope);
        if (!compressionSensitive)
        {
            return state.UsedVolumeTicks + Nesting.UsedVolumeDelta(placed, reserve);
        }
        // Zero load is the candidate's largest volume and appending can only compress existing
        // supports, so this upper bound is safe; only candidates near the boundary pay the
        // non-local recomputation.
        var upperBound = state.UsedVolumeTicks + Nesting.OccupiedVolume(reserve);
        return upperBound <= usable
            ? upperBound
            : Loads.UsedVolumeWithCurrentLoads([.. placed, reserve]);
    }

    private static CandidateScore Score(ContainerState state, Point point, Dimensions dims)
    {
        var inner = state.Container.InnerDimensions;
        var newHeight = Math.Max(state.MaxZ, point.Z + dims.Height.Ticks);
        var rx = inner.Length.Ticks - (point.X + dims.Length.Ticks);
        var ry = inner.Width.Ticks - (point.Y + dims.Width.Ticks);
        var rz = inner.Height.Ticks - (point.Z + dims.Height.Ticks);
        return new CandidateScore(point.Z, newHeight, (Int128)rx * ry + rz, point.Y, point.X);
    }
}
