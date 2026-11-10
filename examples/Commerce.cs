using System;
using Packvium.Commerce;
using Packvium.Serialization;

namespace Packvium.Examples;

/// <summary>
/// Quote a shipment, apply a policy rule, and inspect a catalog version.
///
/// Run it:
///
///     dotnet run --project examples -- commerce
///
/// Everything the three functions need arrives in one commerce document: the tariffs, the
/// eligibility rules and the catalog versions you publish. A version's number is its
/// position in its list, starting at 1. Nothing here reads the clock: every "which version
/// applies" question is answered from a pin you supply, so the same call replays to the
/// same answer next year.
/// </summary>
public static class Commerce
{
    // One document, three histories. You would normally load this from your own storage.
    private const string Document = """
        {
          "tariffs": [
            {
              "carrier_id": "acme",
              "service_id": "ground",
              "versions": [
                {
                  "effective_at": 0,
                  "dimensional_weight_divisor": 5000,
                  "cost_per_dimensional_kg_minor": {"zone-a": 450, "zone-b": 610},
                  "minimum_charge_minor": 900,
                  "fuel_surcharge_permille": 120,
                  "accessorials": [
                    {"accessorial_id": "liftgate", "flat_charge_minor": 250},
                    {"accessorial_id": "residential", "permille_of_base": 75}
                  ]
                },
                {
                  "effective_at": 1000,
                  "dimensional_weight_divisor": 4000,
                  "cost_per_dimensional_kg_minor": {"zone-a": 480},
                  "minimum_charge_minor": 950,
                  "fuel_surcharge_permille": 140,
                  "accessorials": [{"accessorial_id": "liftgate", "flat_charge_minor": 275}]
                }
              ]
            }
          ],
          "policy_rules": [
            {
              "rule_id": "no-hazmat-air",
              "versions": [
                {
                  "scope": "hazmat",
                  "action": "reject",
                  "priority": 10,
                  "effective_at": 0,
                  "reason": "class 1.4 is not accepted on air services",
                  "predicates": [{"scope": "hazmat", "field": "un_class", "operator": "equals", "value": "1.4"}]
                }
              ]
            }
          ],
          "catalogs": [
            {
              "catalog_id": "dc-12",
              "versions": [
                {
                  "effective_at": 0, "published_at": 0, "note": "initial",
                  "snapshot": {
                    "items": [{"id": "sku-1", "dimensions_mm": [100, 200, 300], "weight_g": 1200}],
                    "cartons": [{"id": "box-m", "inner_dimensions_mm": [320, 240, 180], "max_payload_g": 15000, "cost_minor": 85}]
                  }
                },
                {"rollback_to": 1, "published_at": 900, "effective_at": 900, "note": "revert the weight correction"}
              ]
            }
          ]
        }
        """;

    public static void Run()
    {
        var document = JsonReader.Parse(Document);

        // 1. Quote: what does this shipment cost? Pinned to one version, or at an instant.
        var pinned = CommerceApi.Quote(document, JsonReader.Parse("""
            {"carrier_id": "acme", "service_id": "ground", "tariff_version": 1, "zone": "zone-a",
             "actual_weight_g": 1200, "volume_mm3": 6000000, "requested_accessorials": ["liftgate"]}
            """));
        Show("a quote pinned to tariff version 1", pinned);
        Console.WriteLine($"   -> the caller pays {Field(pinned, "quote", "total_minor")} minor units");

        var effective = CommerceApi.Quote(document, JsonReader.Parse("""
            {"carrier_id": "acme", "service_id": "ground", "as_of": 1500, "zone": "zone-a",
             "actual_weight_g": 1200, "volume_mm3": 6000000, "requested_accessorials": ["liftgate"]}
            """));
        Console.WriteLine($"\n   as of instant 1500 the tariff is version {Field(effective, "quote", "tariff_version")}, "
                          + $"and the price is {Field(effective, "quote", "total_minor")}");

        // A request the model cannot answer is not an exception: it is a result with a status,
        // a code from a closed set, and the fields that say what was missing.
        var unpriceable = CommerceApi.Quote(document, JsonReader.Parse("""
            {"carrier_id": "acme", "service_id": "ground", "tariff_version": 1, "zone": "zone-nowhere",
             "actual_weight_g": 1200, "volume_mm3": 6000000}
            """));
        Show("a zone this tariff does not price", unpriceable);

        // 2. Policy: may this shipment go at all?
        var decision = CommerceApi.EvaluatePolicy(document,
            JsonReader.Parse("""{"scope": "hazmat", "context": {"un_class": "1.4"}, "as_of": 0}"""));
        Show("a policy decision, with the rule that made it", decision);
        var allowed = CommerceApi.EvaluatePolicy(document,
            JsonReader.Parse("""{"scope": "hazmat", "context": {"un_class": "9"}, "as_of": 0}"""));
        Console.WriteLine($"\n   nothing matched, so the shipment is allowed with no citation: {Field(allowed, "decision", "citation")}");

        // 3. Catalog: which master data was this decision made against?
        var catalog = CommerceApi.CatalogVersionInfo(document,
            JsonReader.Parse("""{"catalog_id": "dc-12", "version": 2, "resolved_at": 1700}"""));
        Show("catalog version metadata", catalog);
        Console.WriteLine($"\n   version {Field(catalog, "catalog", "version")} is a rollback of version {Field(catalog, "catalog", "rolled_back_from")}");

        // Storing or comparing a result: use the canonical form, never ToString().
        Console.WriteLine("\n== the canonical form is what you store, log and compare");
        Console.WriteLine(CommerceApi.CanonicalJson(pinned));
    }

    private static void Show(string title, JsonValue result)
    {
        Console.WriteLine($"\n== {title}");
        Console.WriteLine(PythonJson.Dumps(result, sortKeys: false, indent: 2));
    }

    private static string Field(JsonObject result, string section, string key) =>
        PythonJson.Dumps(((JsonObject)result.Require(section)).Require(key), sortKeys: false);
}
