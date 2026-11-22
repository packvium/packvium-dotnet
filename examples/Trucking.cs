using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Serialization;
using Packvium.Units;
using Packvium.Validation;

namespace Packvium.Examples;

/// <summary>
/// Load a delivery van for a three-stop route, within its axle ratings.
///
/// Run it:
///
///     dotnet run --project examples -- trucking
///
/// A van is not a big box. Its payload rides on two axles, and each axle has its own rating that
/// the total payload limit does not protect: a van can be under its payload and still overload
/// the front axle. Its cargo also comes out through one door, in the order the stops come up, so
/// the goods for the first stop must not be walled in by the goods for the last one.
///
/// Three request fields carry that:
///
/// - <c>axles</c> on the container: <c>[front, rear]</c>, each a position along the length and an
///   optional <c>max_load</c>. Checked for every placement against the gross load (payload plus
///   tare), so no plan puts either axle over its rating.
/// - <c>access_directions</c> on the container: the walls cargo can leave through. <c>+x</c> is the
///   wall at the far end of the length axis -- here, the rear doors.
/// - <c>stop_index</c> on each item: which stop it is delivered at, <c>0</c> first.
///
/// After the solve, the <see cref="RouteSequence"/> functions answer the questions a driver asks:
/// in what order do things come off, in what order must they go on, and what can I reach when I
/// open the doors at the first stop?
/// </summary>
public static class Trucking
{
    private static readonly string[] RearDoors = ["+x"];

    // Length runs from the cab bulkhead (x = 0) to the rear doors. The axle positions are measured
    // along that same axis, which is what lets the engine take moments about them.
    private static readonly Axle FrontAxle = new(Length.Mm("600"), Weight.Parse("1650 kg"));

    private static readonly Axle RearAxle = new(Length.Mm("3500"), Weight.Parse("2200 kg"));

    // The effort budget decides where the search stops, so every run gives this same plan; the
    // time limit is only a fuse far above what this needs (see Reproducibility.cs).
    // `max_containers: 1` because there is one van: anything that does not fit is reported, not
    // sent in a second vehicle.
    private static readonly PackingConfig Config = new(SolverProfile.Balanced, 60_000, maxContainers: 1,
        effortBudget: new EffortBudget(MaxRestarts: 8));

    // Tare matters here: axle ratings are gross limits, and the empty van already puts most of the
    // front rating to use before anything is loaded.
    private static Container Van(Axle? frontAxle = null) =>
        new("van", Dimensions.Mm("4200", "1800", "1900"), tareWeight: Weight.Parse("2400 kg"),
            maxPayload: Weight.Parse("1300 kg"), axles: (frontAxle ?? FrontAxle, RearAxle), accessDirections: RearDoors);

    private static Item[] Manifest(bool withRoute = true)
    {
        long? Stop(long index) => withRoute ? index : null;
        return
        [
            new("bakery-rack", Dimensions.Mm("800", "600", "1700"), Weight.Parse("90 kg"), 2, keepUpright: true,
                stopIndex: Stop(0)),
            new("drinks-pallet", Dimensions.Mm("1200", "800", "1100"), Weight.Parse("420 kg"), keepUpright: true,
                mustBeOnFloor: true, stopIndex: Stop(1)),
            new("parcel", Dimensions.Mm("600", "400", "400"), Weight.Parse("18 kg"), 6, stopIndex: Stop(2)),
            new("dry-goods-pallet", Dimensions.Mm("1200", "800", "1400"), Weight.Parse("260 kg"), keepUpright: true,
                stopIndex: Stop(2)),
        ];
    }

    public static void Run()
    {
        Python.Section("1. The plan: nothing for a later stop stands in an earlier stop's way out");
        var result = new Packer(Config).Pack(Manifest(), [Van()]);
        var packed = result.Containers[0];
        var placements = packed.Placements;
        Console.WriteLine($"  status: {result.Status.Value()}, left behind: {result.Unpacked.Length}");
        foreach (var placement in placements.OrderBy(p => (p.Position.X, p.Position.Y, p.Position.Z)))
        {
            Console.WriteLine($"    stop {placement.Instance.Item.StopIndex}  {placement.Instance.Id,-20}"
                              + $" x={Millimetres(placement.Position.X),6} mm  y={Millimetres(placement.Position.Y),6} mm");
        }
        Python.Say("",
            "x is the distance from the bulkhead, so the rear doors are at x = 4200 mm. The",
            "bakery racks sit at the front, but the aisle beside them runs clear to the doors:",
            "the rule is that nothing due later blocks every way out, not that stop 0 is last in.");

        Python.Section("2. Axle loads against their ratings");
        var (front, rear) = AxleLoadsKg(result);
        Console.WriteLine($"  payload: {packed.PayloadWeight.DecimalText("kg", 1)} kg of 1300 kg allowed");
        Console.WriteLine($"  front axle: {front} kg of {FrontAxle.MaxLoad!.Value.DecimalText("kg", 1)} kg");
        Console.WriteLine($"  rear axle:  {rear} kg of {RearAxle.MaxLoad!.Value.DecimalText("kg", 1)} kg");
        var tight = new Packer(Config).Pack(Manifest(), [Van(new Axle(Length.Mm("600"), Weight.Parse("1450 kg")))]);
        Console.WriteLine();
        Console.WriteLine("  the same load with a 1450 kg front axle rating:");
        foreach (var unpacked in tight.Unpacked)
        {
            Console.WriteLine($"    left behind: {unpacked.Instance.Id}, reason {unpacked.Reason} ({unpacked.Proof.Level})");
        }
        Python.Say("",
            "The axle rule refuses a placement rather than reporting an overload afterwards.",
            "Note the reason: an item the axles cannot carry is reported as the generic",
            "`no_feasible_placement`, not as an axle problem, so when a load comes up short on",
            "a vehicle with axle ratings, check the reactions before shopping for a bigger van.");

        Python.Section("3. Unloading order along the route, and the loading order it implies");
        var boxes = placements.Select(placement => placement.EnvelopeBox).ToList();
        var stops = placements.Select(placement => placement.Instance.Item.StopIndex).ToList();
        var inside = packed.Container.InnerDimensions;
        var unloading = RouteSequence.SafeRouteRemovalOrder(boxes, stops, inside, RearDoors);
        for (var step = 0; step < unloading.Count; step++)
        {
            Console.WriteLine($"    off {step + 1,2}: stop {stops[unloading[step]]}  {placements[unloading[step]].Instance.Id}");
        }
        // Loading is unloading played backwards: whatever comes off last goes on first.
        // ReplayLoadingOrder checks that independently -- every item supported by what is already
        // in, and each one able to travel in through the doors -- and throws if not.
        var loading = Enumerable.Reverse(unloading).ToList();
        RouteSequence.ReplayLoadingOrder(boxes, inside, loading, RearDoors);
        Console.WriteLine();
        Console.WriteLine($"  load in this order: {string.Join(", ", loading.Select(index => placements[index].Instance.Id))}");

        Python.Section("4. What the driver can reach on opening the doors at stop 0");
        foreach (var reach in RouteSequence.PlacementReachability(boxes, inside, stops, RearDoors))
        {
            if (stops[reach.Index] != 0)
            {
                continue;
            }
            var blockers = reach.BlockedByNeighbors.Union(reach.BlockedBySupport)
                .Select(index => placements[index].Instance.Id).Order(StringComparer.Ordinal).ToList();
            Console.WriteLine($"    {placements[reach.Index].Instance.Id}: reachable={(reach.Reachable ? "True" : "False")}"
                              + (blockers.Count > 0 ? $", behind {string.Join(", ", blockers)}" : string.Empty));
        }
        Python.Say("",
            "Reachability is a snapshot, not a plan: one rack is behind the other, which is",
            "fine because both are for this stop and the first one comes off first.");

        Python.Section("5. The same goods without `stop_index`");
        var unrouted = new Packer(Config).Pack(Manifest(withRoute: false), [Van()]);
        var unroutedPlacements = unrouted.Containers[0].Placements;
        var due = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["bakery-rack"] = 0, ["drinks-pallet"] = 1, ["parcel"] = 2, ["dry-goods-pallet"] = 2,
        };
        try
        {
            RouteSequence.SafeRouteRemovalOrder([.. unroutedPlacements.Select(p => p.EnvelopeBox)],
                [.. unroutedPlacements.Select(p => due[p.Instance.Item.Id])], inside, RearDoors);
        }
        catch (RouteSequenceException stuck)
        {
            var names = stuck.Stuck.Select(index => unroutedPlacements[index].Instance.Id).Order(StringComparer.Ordinal);
            Console.WriteLine($"  status {unrouted.Status.Value()}, every item packed -- and at stop {stuck.Stop}"
                              + $" the driver cannot get these out: {string.Join(", ", names)}");
        }
        Python.Say("",
            "Without the route the plan is still a valid packing, and still within the axle",
            "ratings; it just cannot be delivered in order. The route is only enforced when",
            "you state it.");
    }

    private static string Millimetres(long ticks) => new Length(ticks).DecimalText("mm", 1);

    /// <summary>
    /// The exact axle reactions from the result document, shown in kilograms. They arrive as a
    /// numerator over a shared denominator so no engine has to round them; rounding happens here,
    /// for display only, half to even as Python's <c>round</c> does.
    /// </summary>
    private static (string Front, string Rear) AxleLoadsKg(PackingResult result)
    {
        var container = (JsonObject)((JsonArray)ResultWriter.Write(result).Require("containers")).Items[0];
        var reactions = (JsonObject)container.Require("axle_reactions");
        var denominator = BigInteger.Parse(Text(reactions, "denominator"), System.Globalization.CultureInfo.InvariantCulture);
        string Kilograms(string key)
        {
            var numerator = BigInteger.Parse(Text(reactions, key), System.Globalization.CultureInfo.InvariantCulture);
            return new Weight((long)RoundHalfEven(numerator, denominator)).DecimalText("kg", 1);
        }
        return (Kilograms("front_numerator"), Kilograms("rear_numerator"));
    }

    private static BigInteger RoundHalfEven(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (remainder.Sign < 0)
        {
            quotient -= 1;
            remainder += denominator;
        }
        var twice = remainder * 2;
        return twice > denominator || (twice == denominator && !quotient.IsEven) ? quotient + 1 : quotient;
    }

    private static string Text(JsonObject value, string key) => ((JsonString)value.Require(key)).Value;
}
