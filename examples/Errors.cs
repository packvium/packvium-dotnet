using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Packvium.Domain;
using Packvium.Serialization;
using Node = System.Text.Json.Nodes.JsonObject;
using NodeArray = System.Text.Json.Nodes.JsonArray;
using JsonNode = System.Text.Json.Nodes.JsonNode;

namespace Packvium.Examples;

/// <summary>
/// Turn a refused request into a useful answer, the way a web handler would.
///
/// Run it:
///
///     dotnet run --project examples -- errors
///
/// A packing service takes JSON from a caller it does not control. Most failures are the
/// caller's: a quantity of zero, a negative width, a unit nobody has heard of. Those throw
/// <see cref="InvalidRequestException"/> before anything is solved, with three properties a
/// program can use:
///
/// - <c>Reason</c>: one of a closed set (<c>missing_field</c>, <c>wrong_type</c>,
///   <c>below_minimum</c>, ...), the thing to branch on;
/// - <c>Field</c>: a JSON Pointer to the bad value in the request the caller sent, such as
///   <c>/items/0/quantity</c>;
/// - <c>Message</c>: <c>invalid_request: &lt;field&gt;: &lt;detail&gt;</c>, the same text in every
///   Packvium engine, fit for a log line.
///
/// <see cref="FixedPlacementException"/> is a subclass for items already in place that cannot be
/// where the request says. And a request that is valid but does not fit completely is not an
/// error at all: it is a result with <c>unpacked_items</c>.
/// </summary>
public static class Errors
{
    private const string Order = """
        {
          "units": {"length": "mm"},
          "configuration": {"time_limit_ms": 60000, "effort_budget": {"max_restarts": 4}},
          "items": [
            {"id": "mug", "quantity": 2, "weight": "380 g",
             "dimensions": {"length": "100", "width": "100", "height": "120"}},
            {"id": "teapot", "quantity": 1, "weight": "1.2 kg",
             "dimensions": {"length": "220", "width": "160", "height": "180"}}
          ],
          "containers": [{"id": "box", "inner_dimensions": {"length": "300", "width": "250", "height": "200"}}]
        }
        """;

    public static void Run()
    {
        Python.Section("1. Refused before solving: one reason, one pointer, one message");
        Show("a valid order", Broken(_ => { }));
        Show("quantity 0", Broken(r => Item(r, 0)["quantity"] = 0));
        Show("a negative width", Broken(r => Item(r, 1)["dimensions"]!["width"] = "-160"));
        Show("a missing height", Broken(r => ((Node)Item(r, 1)["dimensions"]!).Remove("height")));
        Show("a float where a measure belongs", Broken(r => Item(r, 1)["dimensions"]!["width"] = 160.5));
        Show("the same container id twice", Broken(r => Containers(r).Add(Containers(r)[0]!.DeepClone())));
        Show("an unknown solver profile", Broken(r => r["configuration"]!["solver_profile"] = "turbo"));
        Python.Say("",
            "A float is refused as a measure on purpose: binary floating point cannot hold most",
            "decimal lengths exactly, and every length here is exact. Send \"160.5\" as a string.",
            "When several values are wrong, the first is reported, in a fixed order every",
            "engine shares: units, configuration, items, containers, fixed placements.");

        Python.Section("2. Items already in place that cannot be there");
        // Turned (`WLH`: its width along the box's length) so the mugs still fit beside it.
        Show("a teapot already in the box, packed around", Broken(r => r["fixed_placements"] = new NodeArray(OnBoard())));
        Show("a teapot recorded at x = 200 mm, hanging out of a 300 mm box", Broken(r =>
        {
            var teapot = OnBoard();
            teapot["position"] = new Node { ["x"] = "200" };
            r["fixed_placements"] = new NodeArray(teapot);
        }));
        Show("a mug and the teapot recorded in the same place", Broken(r => r["fixed_placements"] = new NodeArray(
            OnBoard(), new Node { ["item_type"] = "mug", ["container_type"] = "box", ["orientation"] = "LWH" })));
        Show("an orientation code that does not exist", Broken(r =>
        {
            var teapot = OnBoard();
            teapot["orientation"] = "XYZ";
            r["fixed_placements"] = new NodeArray(teapot);
        }));
        Python.Say("",
            "`cannot_hold` means the fixed set is well formed but is not a valid packing on its",
            "own, and `field` is the whole list. `malformed` points at the one bad value.");

        Python.Section("3. Not an error: a valid order that does not fit");
        Show("a teapot too tall for any box", Broken(r => Item(r, 1)["dimensions"]!["height"] = "450"));
        Python.Say("",
            "The caller asked a fair question and got an answer: the rest of the order is",
            "packed, and `unpacked_items` says what is not and why. Retrying it will not help,",
            "and neither will treating it as a 4xx.");

        Python.Section("4. Two refusals that do not carry a pointer");
        foreach (var (label, change) in new (string, Action<Node>)[]
                 {
                     ("an unknown objective", r => r["configuration"]!["objective"] = "cheapest"),
                     ("an unknown door", r => Containers(r)[0]!["access_directions"] = new NodeArray("rear")),
                 })
        {
            try
            {
                JsonApi.PackFromJson(JsonReader.Parse(Broken(change)));
            }
            catch (InvalidRequestException)
            {
                Console.WriteLine($"  {label}: {nameof(InvalidRequestException)}");
            }
            catch (ArgumentException error)
            {
                Console.WriteLine($"  {label}: {error.GetType().Name}: {error.Message[..Math.Min(70, error.Message.Length)]}");
            }
        }
        Python.Say("",
            "Both are `ArgumentException`s, so a handler that ends with `catch (ArgumentException)` still turns",
            "them into a 422 -- but they have no `reason` or `field` to highlight, only a message.");
    }

    /// <summary>What a POST /pack endpoint would do: a status code and a JSON body.</summary>
    private static (int Status, JsonObject Body) Handle(string body)
    {
        var request = JsonReader.Parse(body);
        JsonObject result;
        try
        {
            result = JsonApi.PackFromJson(request);
        }
        catch (FixedPlacementException error)
        {
            // A subclass of InvalidRequestException, so it must be caught first. Its detail names
            // the physical problem (a collision, too much weight), which is what a dock operator
            // needs to hear.
            return (422, Refusal(error, $"The items already loaded cannot stay as recorded: {error.Detail}."));
        }
        catch (InvalidRequestException error)
        {
            return (422, Refusal(error, $"Please check {Where(request, error.Field)}: {error.Detail}."));
        }
        // Not an error: the caller gets the plan, and a clear list of what is not in it.
        var leftOut = result.Get("unpacked_items") is JsonArray unpacked
            ? unpacked.Items.Select(entry => ((JsonObject)entry).Require("item_id"))
            : [];
        return (200, new JsonObject([
            Member("status", result.Require("status")),
            Member("boxes", new JsonInteger(((JsonArray)result.Require("containers")).Items.Length)),
            Member("left_out", new JsonArray([.. leftOut])),
        ]));
    }

    private static JsonObject Refusal(InvalidRequestException error, string message) => new([
        Member("code", new JsonString(error.Code)), Member("reason", new JsonString(error.Reason)),
        Member("field", new JsonString(error.Field)), Member("message", new JsonString(message)),
    ]);

    private static KeyValuePair<string, JsonValue> Member(string key, JsonValue value) => new(key, value);

    /// <summary>
    /// <c>/items/1/dimensions/width</c> becomes <c>item "teapot", dimensions/width</c>. The pointer
    /// is exact but written for programs. People know their items by id, so the nearest object
    /// that has one names the place, and the rest of the pointer says which value in it.
    /// </summary>
    private static string Where(JsonValue request, string pointer)
    {
        var tokens = pointer.Split('/').Skip(1).ToList();
        var values = Resolve(request, tokens);
        for (var depth = values.Count - 1; depth > 0; depth--)
        {
            if (values[depth] is JsonObject value && value.Get("id") is { } id)
            {
                var kind = tokens[depth - 2].TrimEnd('s');
                var rest = string.Join("/", tokens.Skip(depth));
                return $"{kind} \"{((JsonString)id).Value}\"" + (rest.Length > 0 ? $", {rest}" : string.Empty);
            }
        }
        return pointer.Length > 0 ? pointer : "the request";
    }

    /// <summary>Every value along a JSON Pointer, root first, so a message can name the item by id.</summary>
    private static List<JsonValue> Resolve(JsonValue document, List<string> tokens)
    {
        var values = new List<JsonValue> { document };
        foreach (var token in tokens)
        {
            var key = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            JsonValue? next = values[^1] switch
            {
                JsonArray list when key.All(char.IsAsciiDigit) && key.Length > 0
                    && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < list.Items.Length
                    => list.Items[index],
                JsonObject members when members.Contains(key) => members.Require(key),
                _ => null,
            };
            if (next is null)
            {
                break;
            }
            values.Add(next);
        }
        return values;
    }

    private static void Show(string label, string request)
    {
        var (status, body) = Handle(request);
        Console.WriteLine($"  {label}");
        Console.WriteLine($"    HTTP {status}  {PythonJson.Dumps(body, sortKeys: false, spaced: true)}");
    }

    private static string Broken(Action<Node> change)
    {
        var request = (Node)JsonNode.Parse(Order)!;
        change(request);
        return request.ToJsonString();
    }

    private static Node OnBoard() => new()
    {
        ["item_type"] = "teapot", ["container_type"] = "box", ["orientation"] = "WLH",
        ["position"] = new Node { ["x"] = "0", ["y"] = "0", ["z"] = "0" },
    };

    private static JsonNode Item(Node request, int index) => request["items"]![index]!;

    private static NodeArray Containers(Node request) => (NodeArray)request["containers"]!;
}
