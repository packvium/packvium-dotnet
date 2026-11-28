using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Packing;

/// <summary>
/// Every malformed request of the request-errors corpus (<c>conformance/request_errors/corpus.json</c>)
/// is refused on the object path with the refusal the JSON path gives, as the reference's
/// <c>test_request_objects_corpus.py</c> holds it. Each case is built as objects with the JSON
/// adapter's own builders, but without the JSON rules, and passed to <see cref="Packer"/>. A refusal
/// while an object is built must carry the JSON path's exception type, code and reason with an
/// empty field; a refusal from <c>Pack</c> must carry its field and message too. A case that has no
/// object form, or whose difference is kept on purpose, is listed with why; a listed case that
/// starts matching fails, so it has to leave the list.
/// </summary>
public sealed class RequestObjectsCorpusTests
{
    private const string UnknownKey = "an object has no place for a key its class does not declare";
    private const string Coerced = "the adapter coerces it, so the objects it builds are a different, valid request";
    private const string Typed = "a typed parameter cannot hold it";
    private const string Letters = "the adapter reads text as the sequence of its letters, so the objects hold other values";
    private const string Output = "the output units are arguments of the result writer, not part of the objects packed";
    private const string ConstructorFirst = "two faults: a constructor refuses its own value as the object is built, "
                                            + "before Pack walks the request in the JSON order";
    private const string NegativePosition = "a Point cannot hold a negative coordinate; the measure it is read with "
                                            + "refuses it as negative_measure, where the JSON path names the placement";

    /// <summary>Cases with no object form: the value cannot be held, or the adapter changes it.</summary>
    private static readonly Dictionary<string, string> NotObjects = new(StringComparer.Ordinal)
    {
        ["request-not-object"] = "a request that is not an object has no objects",
        ["items-missing"] = "Pack takes the items as an argument",
        ["items-not-list"] = "Pack takes the items as a sequence of Item",
        ["item-not-object"] = "an item that is not an object is not an Item",
        ["containers-missing"] = "Pack takes the containers as an argument",
        ["configuration-not-object"] = "a configuration that is not an object is not a PackingConfig",
        ["effort-not-object"] = "an effort budget that is not an object is not an EffortBudget",
        ["container-made-to-measure-not-object"] = "a machine that is not an object is not a MadeToMeasure",
        ["container-deformable-boundary-not-object"] = "the object holds the bag's kind, not a block",
        ["container-deformable-boundary-kind-missing"] = "the object holds the bag's kind, not a block",
        ["item-id-missing"] = "Item requires an id",
        ["item-dimensions-missing"] = "Item requires dimensions",
        ["item-side-missing"] = "Dimensions requires all three sides",
        ["container-made-to-measure-minimum-missing"] = "MadeToMeasure requires a minimum",
        ["container-made-to-measure-increment-missing"] = "MadeToMeasure requires an increment",
        ["rate-brackets-not-list"] = "RateTable holds its brackets as a sequence",
        ["solver-profile-unknown"] = "SolverProfile has no such member",
        ["configuration-key-unknown-before-bad-value"] = "SolverProfile has no such member, and " + UnknownKey,
        ["configuration-key-unknown"] = UnknownKey,
        ["configuration-key-unknown-null"] = UnknownKey,
        ["configuration-key-unknown-escaped"] = UnknownKey,
        ["configuration-keys-unknown-first-in-code-point-order"] = UnknownKey,
        ["effort-key-unknown"] = UnknownKey,
        ["container-deformable-boundary-key-unknown"] = UnknownKey,
        ["container-made-to-measure-key-unknown"] = UnknownKey,
        ["unknown-configuration-key-before-its-values"] = UnknownKey,
        ["unsupported-before-units"] = "an unsupported field has no object form",
        ["unsupported-before-an-item"] = "an unsupported field has no object form",
        ["container-flat-bag-with-access-directions"] = "an empty list is how an object leaves the field out",
        ["item-id-number"] = Coerced,
        ["item-quantity-text"] = Coerced,
        ["item-accepts-stacking-groups-not-list"] = Coerced,
        ["container-access-directions-not-list"] = Coerced,
        ["item-quantity-beyond-exact"] = Typed + ": Item.Quantity is an int",
        ["effort-limit-text"] = Typed,
        ["item-stacking-group-number"] = Typed,
        ["item-accepts-stacking-groups-entry-number"] = Typed,
        ["fixed-position-unparsable"] = "a Point holds integer ticks, not text",
        ["fixed_placements.position.x=\"-1\""] = NegativePosition,
        ["fixed_placements.position.x=-1"] = NegativePosition,
        ["fixed_placements.position.y=\"-1\""] = NegativePosition,
        ["fixed_placements.position.y=-1"] = NegativePosition,
        ["fixed_placements.position.z=\"-1\""] = NegativePosition,
        ["fixed_placements.position.z=-1"] = NegativePosition,
        // A closed vocabulary the object holds as an enum member cannot hold a stranger.
        ["item-allowed-rotations-not-list"] = Letters,
        ["item-allowed-rotation-unknown"] = "Rotation has no such member",
        ["item-allowed-rotation-lowercase"] = "Rotation has no such member",
        ["item-shape-type-unknown"] = "ShapeType has no such member",
        ["fixed-orientation-unknown"] = "Rotation has no such member",
        ["configuration-solvers-not-list"] = Letters,
        ["output-not-object"] = Output,
        ["output-length-unit-unknown"] = Output,
        ["output-weight-unit-unknown"] = Output,
        ["output-before-configuration"] = Output,
    };

    /// <summary>Cases the object path answers differently on purpose.</summary>
    private static readonly Dictionary<string, string> DifferentOnPurpose = new(StringComparer.Ordinal)
    {
        ["configuration-objective-unknown"] = "an unknown objective keeps UnknownObjectiveException",
        ["configuration-solver-unknown"] = "an unknown solver keeps UnknownSolverException",
        ["container-access-direction-unknown"] = "an unknown direction keeps InvalidDirectionException",
        ["item-accepts-stacking-groups-entry-empty"] = "a set's index counts in code-point order",
        ["an-item-id-before-its-quantity"] = ConstructorFirst,
        ["item-ids-before-containers"] = ConstructorFirst,
        ["container-ids-before-fixed-placements"] = ConstructorFirst,
    };

    /// <summary>Pinned, so the skip set cannot grow without this line changing too.</summary>
    private const int Skipped = 61;

    private sealed record Refusal(Exception? Error, string Stage);

    [Fact]
    public void TheSkipSetIsPinnedAndNamesOnlyCorpusCases()
    {
        var names = Corpus().Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(NotObjects.Keys.Intersect(DifferentOnPurpose.Keys));
        Assert.DoesNotContain(NotObjects.Keys.Concat(DifferentOnPurpose.Keys), name => !names.Contains(name));
        Assert.Equal(Skipped, NotObjects.Count + DifferentOnPurpose.Count);
        Assert.True(names.Count - Skipped > 100, "the corpus quietly emptied itself");
    }

    [Fact]
    public void TheObjectPathRefusesAsTheJsonPathDoes()
    {
        var differences = new List<string>();
        foreach (var (name, request) in Corpus().Where(entry => !IsSkipped(entry.Name)))
        {
            var (json, objects) = (JsonRefusal(request), ObjectRefusalOf(request));
            if (!SameRefusal(json, objects))
            {
                differences.Add($"{name}: json {Describe(json)}; objects at {objects.Stage} {Describe(objects.Error)}");
            }
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    [Fact]
    public void ASkippedCaseStillDiffers()
    {
        var matching = Corpus().Where(entry => IsSkipped(entry.Name)
                                               && SameRefusal(JsonRefusal(entry.Request), ObjectRefusalOf(entry.Request)))
            .Select(entry => entry.Name).ToList();
        Assert.True(matching.Count == 0, "a skip that matches has been fixed, and must leave its list: "
                                         + string.Join(", ", matching));
    }

    private static bool IsSkipped(string name) => NotObjects.ContainsKey(name) || DifferentOnPurpose.ContainsKey(name);

    private static List<(string Name, JsonValue Request)> Corpus()
    {
        var path = Path.Combine(Conformance.ConformanceTests.CorpusDirectory(), "conformance", "request_errors",
            "corpus.json");
        if (!File.Exists(path))
        {
            Assert.Skip(Conformance.ConformanceTests.CorpusAbsent(path));
        }
        var document = (JsonObject)JsonReader.Parse(File.ReadAllBytes(path));
        Assert.Equal("packvium-request-error-corpus/v1", ((JsonString)document.Require("format")).Value);
        return [.. ((JsonArray)document.Require("cases")).Items.Cast<JsonObject>()
            .Select(entry => (((JsonString)entry.Require("name")).Value, entry.Require("request")))];
    }

    private static Exception JsonRefusal(JsonValue request)
    {
        try
        {
            JsonApi.PackFromJson(request);
        }
        catch (Exception error) when (error is InvalidRequestException or UnsupportedFeatureException)
        {
            return error;
        }
        throw new InvalidOperationException("the JSON path answered a malformed request");
    }

    private static Refusal ObjectRefusalOf(JsonValue request)
    {
        PackingConfig config;
        List<Item> items;
        List<Container> containers;
        List<FixedPlacement> fixedPlacements;
        try
        {
            var data = RequestReader.WithoutNullOptionals((JsonObject)request);
            var unit = data.GetPresent("units") is JsonObject units && units.GetPresent("length") is JsonString length
                ? length.Value
                : "mm";
            config = RequestReader.ReadConfiguration(data.GetPresent("configuration") as JsonObject ?? new JsonObject([]), unit);
            items = [.. ((JsonArray)data.Require("items")).Items.Select(raw => RequestReader.ReadItem((JsonObject)raw, unit))];
            containers = [.. ((JsonArray)data.Require("containers")).Items
                .Select(raw => RequestReader.ReadContainer((JsonObject)raw, unit))];
            fixedPlacements = [.. ((data.GetPresent("fixed_placements") as JsonArray)?.Items ?? [])
                .Select(raw => FixedPlacementOf((JsonObject)raw, unit))];
        }
#pragma warning disable CA1031 // What the objects could not be built from is the finding.
        catch (Exception error)
        {
            return new Refusal(error, "constructor");
        }
        try
        {
            new Packer(config).Pack(items, containers, fixedPlacements);
        }
        catch (Exception error)
        {
            return new Refusal(error, "pack");
        }
#pragma warning restore CA1031
        return new Refusal(null, "pack");
    }

    /// <summary>The adapter's fixed placement without the JSON shape rules in front of it.</summary>
    private static FixedPlacement FixedPlacementOf(JsonObject raw, string unit)
    {
        var position = raw.GetPresent("position") as JsonObject ?? new JsonObject([]);
        long Axis(string axis) => Measures.Length(position.Get(axis) ?? new JsonInteger(0), unit).Ticks;
        return new FixedPlacement(((JsonString)raw.Require("item_type")).Value, ((JsonString)raw.Require("container_type")).Value,
            new Point(Axis("x"), Axis("y"), Axis("z")), Rotations.Parse(((JsonString)raw.Require("orientation")).Value),
            JsonValues.WholeNumber(raw.Get("container_instance") ?? new JsonInteger(1))
            ?? throw new ArgumentException("container_instance is not a whole number"));
    }

    private static bool SameRefusal(Exception json, Refusal objects)
    {
        if (objects.Error is not InvalidRequestException error || json is not InvalidRequestException expected
            || !expected.GetType().IsInstanceOfType(error))
        {
            return false;
        }
        if ((error.Code, error.Reason) != (expected.Code, expected.Reason))
        {
            return false;
        }
        return objects.Stage == "constructor"
            ? error.Field.Length == 0
            : (error.Field, error.Message) == (expected.Field, expected.Message);
    }

    private static string Describe(Exception? error) => error switch
    {
        null => "accepted",
        InvalidRequestException refusal =>
            $"{refusal.GetType().Name}({refusal.Code}, {refusal.Reason}, {refusal.Field}, {refusal.Message})",
        _ => $"{error.GetType().Name}: {error.Message}",
    };
}
