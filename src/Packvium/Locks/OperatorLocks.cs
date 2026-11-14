using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Locks;

/// <summary>
/// Operator locks, expressed as request-derived constraints (<c>packvium.locks</c>). A lock is an
/// ordinary placement constraint supplied through <see cref="ExtensionRegistry"/>, so the locked
/// run is the ordinary run: the same portfolio, the same constraints, the same independent
/// validation. There is no lock-aware solver and no relaxed validator, which is why a lock
/// cannot produce a placement the engine would otherwise refuse.
///
/// This layer is a caller of the packer, never a part of it: nothing below
/// <c>Packvium.Serialization</c> refers to it.
/// </summary>
public static class OperatorLocks
{
    /// <summary>
    /// The rejection code a locked type's non-locked candidate carries, named like every other
    /// constraint code so a diagnostic reads the same whether physics or an operator refused it.
    /// </summary>
    public const string LockViolated = LockConstraint.LockViolated;

    private static readonly JsonObject Empty = new([]);

    /// <summary>
    /// Reads locks out of an execution plan's own placement references: every step of the
    /// container at <paramref name="containerIndex"/> whose item type is one of
    /// <paramref name="itemTypes"/>. Reading the references the plan emits, rather than a second
    /// address format, is the only reason the two can be trusted to mean the same box.
    /// </summary>
    public static ImmutableArray<PlacementLock> LocksFromPlan(JsonObject plan, int containerIndex,
        IEnumerable<string> itemTypes)
    {
        var wanted = itemTypes.ToHashSet(StringComparer.Ordinal);
        var locks = ImmutableArray.CreateBuilder<PlacementLock>();
        foreach (var container in Objects(plan.Get("containers")))
        {
            if (container.Get("container_index") is not JsonInteger index || index.Value != containerIndex)
            {
                continue;
            }
            foreach (var step in Objects(container.Get("steps")))
            {
                var reference = OrEmpty(step.Get("placement"));
                if (reference.Get("item_type") is not JsonString itemType || !wanted.Contains(itemType.Value))
                {
                    continue;
                }
                var ticks = OrEmpty(reference.Get("position_ticks"));
                locks.Add(new PlacementLock(containerIndex, itemType.Value,
                    PythonCoercion.Str(reference.Require("orientation")),
                    (PythonCoercion.Int(ticks.Require("x")), PythonCoercion.Int(ticks.Require("y")),
                        PythonCoercion.Int(ticks.Require("z")))));
            }
        }
        return locks.ToImmutable();
    }

    /// <summary>
    /// The caller's extensions plus the lock constraint, appended after the caller's own
    /// constraints so locking never drops an application's rules. An empty lock set returns
    /// <paramref name="baseRegistry"/> itself.
    /// </summary>
    public static ExtensionRegistry LockRegistry(IEnumerable<PlacementLock> locks, ExtensionRegistry? baseRegistry = null)
    {
        var registry = baseRegistry ?? new ExtensionRegistry();
        var all = locks.ToImmutableArray();
        if (all.IsEmpty)
        {
            return registry;
        }
        return registry with { PlacementConstraints = [.. registry.PlacementConstraints, new LockConstraint(all)] };
    }

    /// <summary>
    /// Re-solves <paramref name="request"/> with <paramref name="locks"/> applied, through the
    /// ordinary packing path, and measures which locks the result kept. A self-contradictory set
    /// throws <see cref="LockSetException"/> before any solve; a lock the solve cannot honour comes
    /// back in <see cref="LockedResolve.Missing"/>. The request and any earlier result are not
    /// touched.
    /// </summary>
    public static LockedResolve ResolveWithLocks(JsonObject request, IEnumerable<PlacementLock> locks,
        ExtensionRegistry? baseRegistry = null)
    {
        var all = locks.ToImmutableArray();
        RefuseAContradictorySet(all, RequestItems(request));
        var result = JsonApi.PackFromJson(request, LockRegistry(all, baseRegistry));
        var placed = Placed(result);
        var missing = all.Where(candidate => !placed.Contains(Key(candidate))).ToImmutableArray();
        return new LockedResolve(all, result, missing.IsEmpty, missing);
    }

    /// <summary>The request's items by id, as the reference reads them to size each lock's box; a later duplicate id wins.</summary>
    private static Dictionary<string, Item> RequestItems(JsonObject request)
    {
        var unit = PythonCoercion.Str(OrEmpty(request.Get("units")).Get("length") ?? new JsonString("mm"));
        var items = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (var raw in Objects(request.Get("items")))
        {
            var item = RequestReader.ReadItem(raw, unit);
            // Only a string id can equal a lock's item type, as a Python dict key would.
            if (raw.Require("id") is JsonString id)
            {
                items[id.Value] = item;
            }
        }
        return items;
    }

    /// <summary>
    /// Every way a lock set can be wrong without a request being consulted. O(L^2) over a set an
    /// operator typed, against a solve that is the expensive thing here. Locks in different
    /// containers describe different boxes and are never compared.
    /// </summary>
    private static void RefuseAContradictorySet(ImmutableArray<PlacementLock> locks, Dictionary<string, Item> items)
    {
        var boxes = new List<(PlacementLock Lock, Dimensions Box)>(locks.Length);
        foreach (var placementLock in locks)
        {
            if (!items.TryGetValue(placementLock.ItemType, out var item))
            {
                throw new LockSetException($"the request has no item type {PythonText.Repr(placementLock.ItemType)} to lock");
            }
            boxes.Add((placementLock, item.Dimensions.Rotated(placementLock.Rotation)));
        }
        for (var index = 0; index < boxes.Count; index++)
        {
            var (first, firstBox) = boxes[index];
            for (var other = index + 1; other < boxes.Count; other++)
            {
                var (second, secondBox) = boxes[other];
                if (first.ContainerIndex != second.ContainerIndex)
                {
                    continue;
                }
                if (first.Slot == second.Slot && string.Equals(first.ItemType, second.ItemType, StringComparison.Ordinal))
                {
                    throw new LockSetException($"the same placement is locked twice: {first.SlotText}");
                }
                if (Overlap(first.PositionTicks, firstBox, second.PositionTicks, secondBox))
                {
                    throw new LockSetException(
                        $"two locks claim overlapping space in container {first.ContainerIndex}: "
                        + $"{first.ItemType} at {first.Orientation}@{first.PositionText} and "
                        + $"{second.ItemType} at {second.Orientation}@{second.PositionText}");
                }
            }
        }
    }

    /// <summary>Half-open box intersection in Int128, so two far-off locks cannot overflow into a false answer.</summary>
    private static bool Overlap((long X, long Y, long Z) first, Dimensions firstBox, (long X, long Y, long Z) second,
        Dimensions secondBox) =>
        Axis(first.X, firstBox.Length.Ticks, second.X, secondBox.Length.Ticks)
        && Axis(first.Y, firstBox.Width.Ticks, second.Y, secondBox.Width.Ticks)
        && Axis(first.Z, firstBox.Height.Ticks, second.Z, secondBox.Height.Ticks);

    private static bool Axis(long start, long extent, long otherStart, long otherExtent) =>
        start < (Int128)otherStart + otherExtent && otherStart < (Int128)start + extent;

    /// <summary>
    /// Every placement of the result, keyed the way a lock addresses one. The container index is
    /// the position in the emitted list, the order the execution plan indexes. A placement
    /// without integer ticks on all three axes cannot match a lock and is skipped.
    /// </summary>
    private static HashSet<(int, string, string, (long, long, long))> Placed(JsonObject result)
    {
        var placed = new HashSet<(int, string, string, (long, long, long))>();
        var index = 0;
        foreach (var container in Objects(result.Get("containers")))
        {
            foreach (var placement in Objects(container.Get("placements")))
            {
                if (Ticks(OrEmpty(placement.Get("position"))) is { } ticks)
                {
                    placed.Add((index, PythonCoercion.Str(placement.Get("item_type") ?? JsonNull.Instance),
                        PythonCoercion.Str(placement.Get("orientation") ?? JsonNull.Instance), ticks));
                }
            }
            index++;
        }
        return placed;
    }

    private static (long, long, long)? Ticks(JsonObject position)
    {
        var axes = new long[3];
        var names = new[] { "x", "y", "z" };
        for (var axis = 0; axis < 3; axis++)
        {
            if (position.Get(names[axis]) is not JsonObject scalar || scalar.Get("ticks") is not { } ticks)
            {
                return null;
            }
            axes[axis] = PythonCoercion.Int(ticks);
        }
        return (axes[0], axes[1], axes[2]);
    }

    private static (int, string, string, (long, long, long)) Key(PlacementLock placementLock) =>
        (placementLock.ContainerIndex, placementLock.ItemType, placementLock.Orientation, placementLock.PositionTicks);

    /// <summary><c>value or {}</c>: a missing, null or empty member reads as an empty object.</summary>
    private static JsonObject OrEmpty(JsonValue? value) => value as JsonObject ?? Empty;

    /// <summary><c>value or ()</c> over a list of objects; anything but an object member is skipped.</summary>
    private static IEnumerable<JsonObject> Objects(JsonValue? value) =>
        value is JsonArray array ? array.Items.OfType<JsonObject>() : [];
}
