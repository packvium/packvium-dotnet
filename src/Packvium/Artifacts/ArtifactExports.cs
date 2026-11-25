using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Packvium.Serialization;

namespace Packvium.Artifacts;

/// <summary>
/// JSON, CSV and print-ready work orders from an operational artifact.
/// Each export is a pure function of the artifact: no solver, carrier, renderer or clock.
///
/// - JSON is the artifact's RFC 8785 canonical form.
/// - CSV has one row per packing step and one per unplaced item. It is RFC 4180: a header row,
///   CRLF after every row, and a field quoted only when it holds a comma, quote, CR or LF.
/// - The work order is one self-contained HTML document: inline print styles, no script, no
///   external resource, ASCII source with entities for the few typographic characters.
///
/// Values are copied, never re-rendered. That includes a CSV field beginning with <c>=</c>:
/// prefixing it would change an identifier a warehouse system matches on. Every export reads the
/// artifact through its canonical form, so it is a function of the bytes every engine agrees
/// on: a <c>1.0</c> held in memory is the <c>1</c> every engine reads back. Each is linear in the
/// artifact's size, plus the canonical form's key sorting.
/// </summary>
public static class ArtifactExports
{
    public static ImmutableArray<string> CsvColumns { get; } =
    [
        "record", "container_index", "container_type", "sequence", "item_type", "orientation",
        "x_ticks", "y_ticks", "z_ticks", "x", "y", "z", "length", "width", "height", "length_unit",
        "reason", "proof_level",
    ];

    private const string Style =
        "body{font-family:system-ui,sans-serif;margin:24px;color:#111}"
        + "h1{font-size:20px}h2{font-size:16px;margin-top:24px}"
        + "table{border-collapse:collapse;width:100%;margin:8px 0 16px}"
        + "th,td{border:1px solid #999;padding:4px 6px;text-align:left;font-size:12px;vertical-align:top}"
        + "th{background:#eee}"
        + ".facts td:first-child{width:28%;font-weight:600}"
        + "@media print{body{margin:0}section.container{break-after:page}tr{break-inside:avoid}}";

    private const string Dash = "&mdash;";

    private static readonly char[] CsvSpecial = [',', '"', '\r', '\n'];

    /// <summary>The artifact's canonical bytes; a document of another format is refused first. O(n + k log k).</summary>
    public static string Json(JsonValue artifact) => OperationalArtifact.CanonicalJson(RequireFormat(artifact));

    public static string Csv(JsonValue artifact)
    {
        var document = Require(artifact);
        var workOrder = Get(document, "work_order");
        var rows = new List<IReadOnlyList<JsonValue?>> { CsvColumns.Select(column => (JsonValue?)new JsonString(column)).ToList() };
        foreach (var container in Items(workOrder, "containers"))
        {
            foreach (var line in Items(container, "lines"))
            {
                var reference = Get(line, "placement");
                var ticks = Get(reference, "position_ticks");
                var (position, dimensions) = (Get(line, "position"), Get(line, "dimensions"));
                rows.Add([
                    new JsonString("step"), Get(reference, "container_index"), Get(container, "container_type"),
                    Optional(line, "sequence"), Get(reference, "item_type"), Get(reference, "orientation"),
                    Get(ticks, "x"), Get(ticks, "y"), Get(ticks, "z"),
                    Get(position, "x"), Get(position, "y"), Get(position, "z"),
                    Get(dimensions, "length"), Get(dimensions, "width"), Get(dimensions, "height"),
                    Get(workOrder, "length_unit"), null, null,
                ]);
            }
        }
        foreach (var unplaced in Items(Get(document, "plan"), "unplaced"))
        {
            var facts = Get(unplaced, "facts");
            rows.Add([
                new JsonString("unplaced"), null, null, null, Get(facts, "item_type"), null, null, null, null,
                null, null, null, null, null, null, null, Get(facts, "reason"), Get(facts, "proof_level"),
            ]);
        }
        var fields = rows.Select(row => row.Select(CsvField).ToList()).ToList();
        return string.Concat(fields.Select(row => string.Join(',', row) + "\r\n"));
    }

    public static string WorkOrderHtml(JsonValue artifact)
    {
        var document = Require(artifact);
        var (plan, workOrder, provenance) = (Get(document, "plan"), Get(document, "work_order"), Get(document, "provenance"));
        var lines = new List<string>
        {
            "<!DOCTYPE html>",
            "<html lang=\"en\">",
            "<head>",
            "<meta charset=\"utf-8\">",
            "<title>Packing work order</title>",
            $"<style>{Style}</style>",
            "</head>",
            "<body>",
            "<h1>Packing work order</h1>",
            "<table class=\"facts\">",
        };
        lines.AddRange(FactRows(document, plan, provenance));
        lines.Add("</table>");
        foreach (var (planContainer, container) in Items(plan, "containers").Zip(Items(workOrder, "containers")))
        {
            lines.AddRange(ContainerSection(planContainer, container, workOrder));
        }
        lines.AddRange(UnplacedSection(Items(plan, "unplaced")));
        lines.AddRange(["</body>", "</html>"]);
        return string.Join('\n', lines) + "\n";
    }

    // ---------------------------------------------------------------------------------- HTML

    private static List<string> FactRows(JsonValue document, JsonValue plan, JsonValue provenance)
    {
        var (facts, replay, solver) = (Get(plan, "facts"), Get(provenance, "replay"), Get(provenance, "solver"));
        var feasibility = Get(facts, "feasibility");
        var because = Get(replay, "because");
        var replayText = Escape(Get(replay, "level")) + (PythonCoercion.Bool(because) ? $" ({Escape(because)})" : string.Empty);
        var solverText = PythonCoercion.Bool(solver)
            ? $"{Escape(Get(solver, "profile"))} / {Escape(Get(solver, "solver"))} / seed {Escape(Get(solver, "seed"))}"
            : Dash;
        var catalogs = string.Join(", ", Items(provenance, "catalog_versions_used")
            .Select(catalog => $"{Escape(Optional(catalog, "catalog_id"))} v{Escape(Optional(catalog, "version"))}"));
        (string Label, string Value)[] rows =
        [
            ("Status", Escape(Get(facts, "status"))),
            ("Objective", OrDash(Get(plan, "objective"))),
            ("Containers", Escape(Get(facts, "container_count"))),
            ("Score", "[" + string.Join(", ", Items(facts, "score").Select(term => Escape(term))) + "]"),
            ("Feasibility", OrDash(feasibility is JsonObject ? Optional(feasibility, "code") : null)),
            ("Replay", replayText),
            ("Solver", solverText),
            ("Catalogs", catalogs.Length > 0 ? catalogs : Dash),
            ("Packvium", Escape(Get(document, "suite_version"))),
        ];
        return [.. rows.Select(row => $"<tr><td>{row.Label}</td><td>{row.Value}</td></tr>")];
    }

    private static List<string> ContainerSection(JsonValue planContainer, JsonValue container, JsonValue workOrder)
    {
        var facts = Get(planContainer, "facts");
        var (weightUnit, lengthUnit) = (Escape(Get(workOrder, "weight_unit")), Escape(Get(workOrder, "length_unit")));
        var lines = new List<string>
        {
            "<section class=\"container\">",
            $"<h2>Container {Ordinal(Get(container, "container_index"))}: {OrDash(Get(container, "container_type"))}</h2>",
            $"<p>Payload {Escape(Get(container, "payload_weight"))} {weightUnit} &middot; "
            + $"Gross {Escape(Get(container, "gross_weight"))} {weightUnit} &middot; "
            + $"Utilization {OrDash(Get(facts, "volume_utilization"))}</p>",
            Get(planContainer, "order") is JsonString { Value: "loading" }
                ? "<p>Order: loading. Follow the steps in sequence.</p>"
                : "<p>Order: unavailable. No safe loading order was supplied, so the steps are not numbered.</p>",
            "<table>",
            $"<thead><tr><th>Step</th><th>Item</th><th>Orientation</th><th>Position x, y, z ({lengthUnit})</th>"
            + $"<th>Size l &times; w &times; h ({lengthUnit})</th><th>Done</th></tr></thead>",
            "<tbody>",
        };
        foreach (var line in Items(container, "lines"))
        {
            var (reference, position, size) = (Get(line, "placement"), Get(line, "position"), Get(line, "dimensions"));
            lines.Add(
                $"<tr><td>{Escape(Optional(line, "sequence") ?? new JsonString(string.Empty))}</td>"
                + $"<td>{Escape(Get(reference, "item_type"))}</td>"
                + $"<td>{Escape(Get(reference, "orientation"))}</td>"
                + $"<td>{Escape(Get(position, "x"))}, {Escape(Get(position, "y"))}, {Escape(Get(position, "z"))}</td>"
                + $"<td>{Escape(Get(size, "length"))} &times; {Escape(Get(size, "width"))} &times; {Escape(Get(size, "height"))}</td>"
                + "<td>&#9744;</td></tr>");
        }
        lines.AddRange(["</tbody>", "</table>", "</section>"]);
        return lines;
    }

    private static List<string> UnplacedSection(ImmutableArray<JsonValue> entries)
    {
        var lines = new List<string> { "<section>", "<h2>Not packed</h2>" };
        if (entries.IsEmpty)
        {
            lines.AddRange(["<p>Every item was packed.</p>", "</section>"]);
            return lines;
        }
        lines.AddRange(["<table>", "<thead><tr><th>Item</th><th>Reason</th><th>Proof</th></tr></thead>", "<tbody>"]);
        foreach (var entry in entries)
        {
            var facts = Get(entry, "facts");
            lines.Add($"<tr><td>{OrDash(Get(facts, "item_type"))}</td><td>{OrDash(Get(facts, "reason"))}</td>"
                      + $"<td>{OrDash(Get(facts, "proof_level"))}</td></tr>");
        }
        lines.AddRange(["</tbody>", "</table>", "</section>"]);
        return lines;
    }

    /// <summary>
    /// The one rendering every engine shares: a string as itself, an integer in decimal, null as
    /// nothing. Anything else -- a boolean, a float, an object -- has a different default spelling
    /// in each language, so it is refused rather than printed several ways.
    /// </summary>
    private static string Text(JsonValue? value) => value switch
    {
        null or JsonNull => string.Empty,
        JsonString text => text.Value,
        JsonInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        _ => throw new OperationalArtifactException("invalid_value",
            $"a {PythonCoercion.TypeName(value)} has no single rendering in a work order"),
    };

    /// <summary>
    /// A container's 1-based number on the sheet. Computed, so it is checked like <see cref="Text"/>:
    /// <c>0.5 + 1</c> would print <c>1.5</c> in one engine and be refused in another.
    /// </summary>
    private static string Ordinal(JsonValue index) => index is JsonInteger integer
        ? (integer.Value + 1).ToString(CultureInfo.InvariantCulture)
        : throw new OperationalArtifactException("invalid_value", "a container index is not an integer");

    private static string Escape(JsonValue? value) => new StringBuilder(Text(value))
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;")
        .ToString();

    private static string OrDash(JsonValue? value) => value is null or JsonNull ? Dash : Escape(value);

    // ----------------------------------------------------------------------------------- CSV

    private static string CsvField(JsonValue? value)
    {
        var text = Text(value);
        return text.IndexOfAny(CsvSpecial) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    // ------------------------------------------------------------------------------- reading

    /// <summary>
    /// An export reads only a document it knows how to read, and says so when it cannot. It reads
    /// the artifact through its canonical form, so it is a function of the bytes every engine
    /// agrees on and not of how this one happens to hold them in memory.
    /// </summary>
    private static JsonObject Require(JsonValue artifact) =>
        (JsonObject)JsonReader.Parse(OperationalArtifact.CanonicalJson(RequireFormat(artifact)));

    private static JsonValue RequireFormat(JsonValue artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if ((artifact as JsonObject)?.Get("format") is not JsonString { Value: OperationalArtifact.Format })
        {
            throw new OperationalArtifactException("unknown_format",
                $"cannot export this format; this exporter reads {OperationalArtifact.Format}");
        }
        return artifact;
    }

    /// <summary>
    /// <c>document[key]</c>: a field the artifact format requires. A document that names the v1
    /// format but lacks the field, or holds something other than an object where one belongs, is
    /// not a v1 document, so it is refused as <c>unknown_format</c>, as the PHP, Java and Go
    /// exporters refuse it, rather than escaping as a cast or lookup failure.
    /// </summary>
    private static JsonValue Get(JsonValue fields, string key) =>
        Object(fields, key).Get(key) ?? throw Malformed($"has no {key}");

    /// <summary><c>document.get(key)</c>: absent is null.</summary>
    private static JsonValue? Optional(JsonValue fields, string key) => Object(fields, key).Get(key);

    private static ImmutableArray<JsonValue> Items(JsonValue fields, string key) =>
        Get(fields, key) is JsonArray array ? array.Items : throw Malformed($"has a {key} that is not a list");

    private static JsonObject Object(JsonValue fields, string key) =>
        fields as JsonObject ?? throw Malformed($"has a {PythonCoercion.TypeName(fields)} where an object with {key} belongs");

    private static OperationalArtifactException Malformed(string problem) =>
        new("unknown_format", $"not a {OperationalArtifact.Format} document: it {problem}");
}
