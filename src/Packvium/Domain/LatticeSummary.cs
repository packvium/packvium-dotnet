using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;

namespace Packvium.Domain;

/// <summary>
/// A compact, O(1)-to-build description of one regular-lattice run, returned instead of one
/// <see cref="Placement"/> per instance when the caller opted out of placement coordinates.
/// <see cref="Expand"/> reconstructs the exact placements -- same order, coordinates and
/// rotation -- so the compact form never loses information. Never built for nesting items.
/// </summary>
public sealed record LatticeSummary
{
    public LatticeSummary(string itemType, Rotation rotation, Dimensions physical, Dimensions envelope, long nx,
        long ny, long layerStep, long clearanceTicks, long count, long weightTicks)
    {
        if (count <= 0)
        {
            throw new ArgumentException("a lattice summary must describe at least one placed instance");
        }
        if (nx <= 0 || ny <= 0)
        {
            throw new ArgumentException("a lattice summary requires positive per-axis capacity");
        }
        ItemType = itemType;
        Rotation = rotation;
        Physical = physical;
        Envelope = envelope;
        Nx = nx;
        Ny = ny;
        LayerStep = layerStep;
        ClearanceTicks = clearanceTicks;
        Count = count;
        WeightTicks = weightTicks;
    }

    public string ItemType { get; }

    public Rotation Rotation { get; }

    public Dimensions Physical { get; }

    public Dimensions Envelope { get; }

    public long Nx { get; }

    public long Ny { get; }

    public long LayerStep { get; }

    public long ClearanceTicks { get; }

    public long Count { get; }

    public long WeightTicks { get; }

    private long PerLayer => Nx * Ny;

    public long FullLayers => Count / PerLayer;

    public long Remainder => Count % PerLayer;

    public long LayersUsed => FullLayers + (Remainder != 0 ? 1 : 0);

    public long TotalWeightTicks => checked(Count * WeightTicks);

    /// <summary>A plain per-item volume sum: a summary is never built for a nesting item.</summary>
    public Int128 UsedVolumeTicks => checked(Count * Physical.Volume);

    /// <summary>The topmost envelope's z2, as the per-item loop would report it.</summary>
    public long MaxZTicks => checked((LayersUsed - 1) * LayerStep + Envelope.Height.Ticks);

    /// <summary>Closed form of <see cref="CentreOfMass.OffsetPpm(Dimensions, IReadOnlyList{Placement})"/> for a uniform lattice. O(1).</summary>
    public long CentreOfMassOffsetPpm(long innerLengthTicks, long innerWidthTicks)
    {
        if (WeightTicks == 0 || Count == 0)
        {
            return 0;
        }
        BigInteger rowsInPartial = Remainder / Nx;
        BigInteger extraColumns = Remainder % Nx;
        var sumX = (BigInteger)FullLayers * Ny * RowTriangular(Nx) + rowsInPartial * RowTriangular(Nx) + RowTriangular(extraColumns);
        var sumY = (BigInteger)FullLayers * Nx * RowTriangular(Ny) + Nx * RowTriangular(rowsInPartial) + extraColumns * rowsInPartial;
        BigInteger weight = WeightTicks;
        var doubledX = weight * (2 * (BigInteger)Envelope.Length.Ticks * sumX
                                 + (BigInteger)Count * (2 * (BigInteger)ClearanceTicks + Physical.Length.Ticks));
        var doubledY = weight * (2 * (BigInteger)Envelope.Width.Ticks * sumY
                                 + (BigInteger)Count * (2 * (BigInteger)ClearanceTicks + Physical.Width.Ticks));
        return CentreOfMass.OffsetPpm((BigInteger)Count * weight, doubledX, doubledY, innerLengthTicks, innerWidthTicks);
    }

    /// <summary>The exact placements the per-item loop would have built, from <c>items[0..count]</c>. O(count).</summary>
    public ImmutableArray<Placement> Expand(IReadOnlyList<ItemInstance> items)
    {
        var placements = ImmutableArray.CreateBuilder<Placement>(checked((int)Count));
        for (long index = 0; index < Count; index++)
        {
            var point = new Point(index % Nx * Envelope.Length.Ticks, index / Nx % Ny * Envelope.Width.Ticks,
                index / PerLayer * LayerStep);
            var position = new Point(point.X + ClearanceTicks, point.Y + ClearanceTicks, point.Z + ClearanceTicks);
            placements.Add(new Placement(items[checked((int)index)], position, Rotation, Physical, point, Envelope));
        }
        return placements.MoveToImmutable();
    }

    private static BigInteger RowTriangular(BigInteger n) => n * (n - 1) / 2;
}
