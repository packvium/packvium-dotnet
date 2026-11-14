using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Packvium.Domain;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Locks;

/// <summary>
/// A lock set that no request could satisfy, raised before any solve: two locks whose boxes
/// overlap, the same placement locked twice, or a lock on an item type the request does not
/// carry. A lock the solve could not honour is an answer instead -- a <see cref="LockedResolve"/>
/// with the lock named in <see cref="LockedResolve.Missing"/> -- because whether it fits is a
/// question about a request, and a set that contradicts itself is not a question at all.
/// </summary>
public sealed class LockSetException(string message) : ArgumentException(message);

/// <summary>
/// One operator lock: an item type pinned to an exact position and orientation in one container
/// of the result. Addressed the way the execution plan addresses a placement -- container index,
/// item type, integer ticks and orientation, never an item id, which is an instance counter
/// engines need not agree on.
/// </summary>
public sealed record PlacementLock
{
    /// <summary>Refuses an address the engine could not have emitted.</summary>
    /// <param name="containerIndex">The container's position in the result's <c>containers</c> list.</param>
    /// <param name="itemType">The <c>item_type</c> of the locked placement.</param>
    /// <param name="orientation">The placement's orientation, one of the six rotation names such as <c>LWH</c>.</param>
    /// <param name="positionTicks">The placement's exact position in ticks (1/16000 mm), x, y and z.</param>
    public PlacementLock(int containerIndex, string itemType, string orientation, (long X, long Y, long Z) positionTicks)
    {
        if (containerIndex < 0)
        {
            throw new ArgumentException("a lock must name a container index");
        }
        if (itemType.Length == 0)
        {
            throw new ArgumentException("a lock must name an item type");
        }
        if (!Rotations.All.Any(rotation => string.Equals(rotation.Value(), orientation, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"{PythonText.Repr(orientation)} is not an orientation this engine emits");
        }
        if (positionTicks.X < 0 || positionTicks.Y < 0 || positionTicks.Z < 0)
        {
            throw new ArgumentException("a lock's position cannot be negative");
        }
        ContainerIndex = containerIndex;
        ItemType = itemType;
        Orientation = orientation;
        PositionTicks = positionTicks;
    }

    /// <summary>The container's position in the result's <c>containers</c> list.</summary>
    public int ContainerIndex { get; }

    /// <summary>The <c>item_type</c> of the locked placement.</summary>
    public string ItemType { get; }

    /// <summary>The rotation name, such as <c>LWH</c>.</summary>
    public string Orientation { get; }

    /// <summary>The exact position in ticks (1/16000 mm).</summary>
    public (long X, long Y, long Z) PositionTicks { get; }

    /// <summary>The orientation and position the lock reserves, which is what a placement fills.</summary>
    public (string Orientation, (long X, long Y, long Z) Position) Slot => (Orientation, PositionTicks);

    internal Rotation Rotation => Rotations.Parse(Orientation);

    /// <summary>The position as the reference prints a tuple: <c>(0, 0, 0)</c>.</summary>
    internal string PositionText => Ticks(PositionTicks);

    /// <summary>The slot as the reference prints it: <c>('LWH', (0, 0, 0))</c>.</summary>
    internal string SlotText => $"({PythonText.Repr(Orientation)}, {PositionText})";

    internal static string Ticks((long X, long Y, long Z) ticks) =>
        string.Create(CultureInfo.InvariantCulture, $"({ticks.X}, {ticks.Y}, {ticks.Z})");
}

/// <summary>
/// A re-solve under a lock set, beside the plan it came from -- never replacing it.
/// <see cref="Preserved"/> is measured against <see cref="Result"/>, not promised by the
/// constraint: forbidding alternatives stops the solver succeeding elsewhere, it does not make
/// it try the locked point.
/// </summary>
public sealed record LockedResolve
{
    /// <summary>Refuses a preserved resolve that is missing a lock, and an unpreserved one that names none.</summary>
    public LockedResolve(ImmutableArray<PlacementLock> locks, JsonObject result, bool preserved,
        ImmutableArray<PlacementLock> missing = default)
    {
        missing = missing.IsDefault ? [] : missing;
        if (preserved && !missing.IsEmpty)
        {
            throw new ArgumentException("a preserved resolve cannot be missing a lock");
        }
        if (!preserved && missing.IsEmpty)
        {
            throw new ArgumentException("an unpreserved resolve must name the locks it could not honour");
        }
        Locks = locks.IsDefault ? [] : locks;
        Result = result;
        Preserved = preserved;
        Missing = missing;
    }

    /// <summary>The locks the re-solve ran under, in the order they were given.</summary>
    public ImmutableArray<PlacementLock> Locks { get; }

    /// <summary>The re-solve's own result document; the approved one is a separate object, untouched.</summary>
    public JsonObject Result { get; }

    /// <summary>Whether every lock is a placement of <see cref="Result"/>.</summary>
    public bool Preserved { get; }

    /// <summary>The locks <see cref="Result"/> does not contain: what an operator is told did not hold.</summary>
    public ImmutableArray<PlacementLock> Missing { get; }
}
