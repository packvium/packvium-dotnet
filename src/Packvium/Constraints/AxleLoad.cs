using System;
using System.Collections.Generic;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Constraints;

/// <summary>
/// Two-point beam statics for a two-axle container: exact fractions over a shared denominator,
/// compared by cross-multiplying, never a rounded intermediate. O(n) in the load units.
/// </summary>
public static class AxleLoad
{
    /// <summary>
    /// <c>(denominator, front_numerator, rear_numerator)</c> exactly. Payload acts at each
    /// item's centre, tare at the container's doubled longitudinal centre.
    /// </summary>
    public static (Int128 Denominator, Int128 Front, Int128 Rear) Reactions((Axle Front, Axle Rear) axles,
        IReadOnlyList<LoadUnit> units, long tareWeightTicks = 0, long tareDoubledCenterX = 0)
    {
        var (front, rear) = axles;
        var (total, doubledWeightedX) = Moments(units, tareWeightTicks, tareDoubledCenterX);
        Int128 denominator = 2 * (rear.Position.Ticks - front.Position.Ticks);
        return (denominator,
            checked(2 * total * rear.Position.Ticks - doubledWeightedX),
            checked(doubledWeightedX - 2 * total * front.Position.Ticks));
    }

    /// <summary>Whether either axle bears more than its own limit, as <c>(code, detail)</c>.</summary>
    public static (string Code, string Detail)? Exceeded((Axle Front, Axle Rear) axles, IReadOnlyList<LoadUnit> units,
        long tareWeightTicks = 0, long tareDoubledCenterX = 0)
    {
        var (front, rear) = axles;
        var (denominator, numeratorFront, numeratorRear) = Reactions(axles, units, tareWeightTicks, tareDoubledCenterX);
        if (front.MaxLoad is { } frontLimit && numeratorFront > checked(frontLimit.Ticks * denominator))
        {
            return ("axle_overloaded", "front");
        }
        if (rear.MaxLoad is { } rearLimit && numeratorRear > checked(rearLimit.Ticks * denominator))
        {
            return ("axle_overloaded", "rear");
        }
        return null;
    }

    /// <summary>
    /// The tightest x-origins that seat an item exactly on either axle's limit, biased to the
    /// safe side. The caller clamps and discards out-of-range results.
    /// </summary>
    public static List<long> BalancedOrigins((Axle Front, Axle Rear) axles, IReadOnlyList<LoadUnit> otherUnits,
        long tareWeightTicks, long tareDoubledCenterX, long itemWeightTicks, long itemLengthTicks)
    {
        var origins = new List<long>();
        if (itemWeightTicks <= 0)
        {
            return origins;
        }
        var (front, rear) = axles;
        Int128 denominator = 2 * (rear.Position.Ticks - front.Position.Ticks);
        var (otherTotal, otherDoubledX) = Moments(otherUnits, tareWeightTicks, tareDoubledCenterX);
        var total = otherTotal + itemWeightTicks;
        if (front.MaxLoad is { } frontLimit)
        {
            var required = checked(2 * total * rear.Position.Ticks - frontLimit.Ticks * denominator - otherDoubledX);
            var doubledCentre = IntMath.CeilDiv(required, itemWeightTicks);
            origins.Add(IntMath.ToLong(IntMath.CeilDiv(doubledCentre - itemLengthTicks, 2)));
        }
        if (rear.MaxLoad is { } rearLimit)
        {
            var allowed = checked(rearLimit.Ticks * denominator + 2 * total * front.Position.Ticks - otherDoubledX);
            var doubledCentre = IntMath.FloorDiv(allowed, itemWeightTicks);
            origins.Add(IntMath.ToLong(IntMath.FloorDiv(doubledCentre - itemLengthTicks, 2)));
        }
        return origins;
    }

    private static (Int128 Total, Int128 DoubledWeightedX) Moments(IReadOnlyList<LoadUnit> units, long tareWeightTicks,
        long tareDoubledCenterX)
    {
        Int128 total = tareWeightTicks;
        var doubledWeightedX = checked((Int128)tareWeightTicks * tareDoubledCenterX);
        foreach (var unit in units)
        {
            total += unit.WeightTicks;
            doubledWeightedX = checked(doubledWeightedX
                                       + (Int128)unit.WeightTicks * (2 * (Int128)unit.Box.Origin.X + unit.Box.Dimensions.Length.Ticks));
        }
        return (total, doubledWeightedX);
    }
}
