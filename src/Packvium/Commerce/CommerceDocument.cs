using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;
using static Packvium.Commerce.CommerceJson;

namespace Packvium.Commerce;

/// <summary>
/// The three append-only histories a request is answered against, parsed from one canonical
/// commerce document. A version's number is its 1-based position in its list. One pass:
/// O(size of the document).
/// </summary>
public sealed class CommerceDocument
{
    private static readonly JsonArray None = new([]);

    private CommerceDocument(CarrierRegistry carriers, PolicyRegistry policies, IReadOnlyDictionary<string, CatalogRegistry> catalogs)
    {
        Carriers = carriers;
        Policies = policies;
        Catalogs = catalogs;
    }

    public CarrierRegistry Carriers { get; }

    public PolicyRegistry Policies { get; }

    public IReadOnlyDictionary<string, CatalogRegistry> Catalogs { get; }

    public static CommerceDocument Load(JsonValue document)
    {
        var payload = Mapping(document, "document");
        Keys(payload, "document", [], ["tariffs", "policy_rules", "catalogs"]);
        return new CommerceDocument(
            LoadTariffs(payload.Get("tariffs") ?? None),
            LoadPolicyRules(payload.Get("policy_rules") ?? None),
            LoadCatalogs(payload.Get("catalogs") ?? None));
    }

    // ---------------------------------------------------------------------------- tariffs

    private static CarrierRegistry LoadTariffs(JsonValue value)
    {
        var registry = new CarrierRegistry();
        var seen = new HashSet<(string, string)>();
        foreach (var (entry, index) in Sequence(value, "document.tariffs").Select((entry, index) => (entry, index)))
        {
            var path = $"document.tariffs[{index}]";
            var fields = Mapping(entry, path);
            Keys(fields, path, ["carrier_id", "service_id", "versions"]);
            var carrierId = Text(fields.Get("carrier_id"), path + ".carrier_id");
            var serviceId = Text(fields.Get("service_id"), path + ".service_id");
            if (!seen.Add((carrierId, serviceId)))
            {
                throw Fail(path, $"duplicate tariff history for {carrierId}/{serviceId}");
            }
            PublishTariffVersions(registry, carrierId, serviceId, fields.Require("versions"), path);
        }
        return registry;
    }

    private static void PublishTariffVersions(CarrierRegistry registry, string carrierId, string serviceId,
        JsonValue value, string parent)
    {
        var versions = Sequence(value, parent + ".versions");
        if (versions.IsEmpty)
        {
            throw Fail(parent + ".versions", "a tariff history needs at least one version");
        }
        for (var index = 0; index < versions.Length; index++)
        {
            var path = $"{parent}.versions[{index}]";
            var fields = Mapping(versions[index], path);
            Keys(fields, path, ["effective_at", "dimensional_weight_divisor", "cost_per_dimensional_kg_minor"],
                ["minimum_charge_minor", "fuel_surcharge_permille", "accessorials"]);
            var zones = Mapping(fields.Require("cost_per_dimensional_kg_minor"), path + ".cost_per_dimensional_kg_minor");
            ImmutableArray<KeyValuePair<string, BigInteger>> costs = [.. zones.Members.Select(zone =>
                new KeyValuePair<string, BigInteger>(zone.Key,
                    Integer(zone.Value, $"{path}.cost_per_dimensional_kg_minor[{Units.PythonText.Repr(zone.Key)}]")))];
            var accessorials = LoadAccessorials(Default(fields, "accessorials", None), path + ".accessorials");
            Model(path, () => registry.Publish(carrierId, serviceId,
                Integer(fields.Get("effective_at"), path + ".effective_at"),
                Integer(fields.Get("dimensional_weight_divisor"), path + ".dimensional_weight_divisor"),
                costs,
                Integer(Default(fields, "minimum_charge_minor", new JsonInteger(0)), path + ".minimum_charge_minor"),
                Integer(Default(fields, "fuel_surcharge_permille", new JsonInteger(0)), path + ".fuel_surcharge_permille"),
                accessorials));
        }
    }

    private static ImmutableArray<KeyValuePair<string, AccessorialCharge>> LoadAccessorials(JsonValue value, string path)
    {
        var charges = new List<KeyValuePair<string, AccessorialCharge>>();
        var entries = Sequence(value, path);
        for (var index = 0; index < entries.Length; index++)
        {
            var entryPath = $"{path}[{index}]";
            var fields = Mapping(entries[index], entryPath);
            Keys(fields, entryPath, ["accessorial_id"], ["flat_charge_minor", "permille_of_base"]);
            var id = Text(fields.Get("accessorial_id"), entryPath + ".accessorial_id");
            if (charges.Any(charge => charge.Key == id))
            {
                throw Fail(entryPath, $"duplicate accessorial_id {Units.PythonText.Repr(id)}");
            }
            var flat = Optional(fields, "flat_charge_minor");
            var permille = Optional(fields, "permille_of_base");
            charges.Add(new(id, Model(entryPath, () => new AccessorialCharge(id,
                flat is null ? null : Integer(flat, entryPath + ".flat_charge_minor"),
                permille is null ? null : Integer(permille, entryPath + ".permille_of_base")))));
        }
        return [.. charges];
    }

    // ----------------------------------------------------------------------- policy rules

    private static string Enum(ImmutableArray<string> vocabulary, string kind, JsonValue? value, string path)
    {
        var text = Text(value, path);
        return vocabulary.Contains(text) ? text : throw Fail(path, $"unsupported {kind} {Units.PythonText.Repr(text)}");
    }

    private static PolicyRegistry LoadPolicyRules(JsonValue value)
    {
        var registry = new PolicyRegistry();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, index) in Sequence(value, "document.policy_rules").Select((entry, index) => (entry, index)))
        {
            var path = $"document.policy_rules[{index}]";
            var fields = Mapping(entry, path);
            Keys(fields, path, ["rule_id", "versions"]);
            var ruleId = Text(fields.Get("rule_id"), path + ".rule_id");
            if (!seen.Add(ruleId))
            {
                throw Fail(path, $"duplicate rule history for {Units.PythonText.Repr(ruleId)}");
            }
            var versions = Sequence(fields.Require("versions"), path + ".versions");
            if (versions.IsEmpty)
            {
                throw Fail(path + ".versions", "a rule history needs at least one version");
            }
            for (var number = 0; number < versions.Length; number++)
            {
                var versionPath = $"{path}.versions[{number}]";
                var version = Mapping(versions[number], versionPath);
                Keys(version, versionPath, ["scope", "action", "predicates", "priority", "effective_at"], ["reason"]);
                Model(versionPath, () => registry.Publish(ruleId,
                    Enum(PolicyVocabulary.Scopes, "PolicyScope", version.Get("scope"), versionPath + ".scope"),
                    Enum(PolicyVocabulary.Actions, "PolicyAction", version.Get("action"), versionPath + ".action"),
                    LoadPredicates(version.Require("predicates"), versionPath + ".predicates"),
                    Integer(version.Get("priority"), versionPath + ".priority"),
                    Integer(version.Get("effective_at"), versionPath + ".effective_at"),
                    Text(Default(version, "reason", new JsonString("")), versionPath + ".reason")));
            }
        }
        return registry;
    }

    private static ImmutableArray<PolicyPredicate> LoadPredicates(JsonValue value, string path)
    {
        var entries = Sequence(value, path);
        var predicates = ImmutableArray.CreateBuilder<PolicyPredicate>(entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var entryPath = $"{path}[{index}]";
            var fields = Mapping(entries[index], entryPath);
            Keys(fields, entryPath, ["scope", "field", "operator"], ["value"]);
            predicates.Add(Model(entryPath, () => new PolicyPredicate(
                Enum(PolicyVocabulary.Scopes, "PolicyScope", fields.Get("scope"), entryPath + ".scope"),
                Text(fields.Get("field"), entryPath + ".field"),
                Enum(PolicyVocabulary.Operators, "PolicyOperator", fields.Get("operator"), entryPath + ".operator"),
                fields.Get("value"))));
        }
        return predicates.MoveToImmutable();
    }

    // --------------------------------------------------------------------------- catalogs

    private static Dictionary<string, CatalogRegistry> LoadCatalogs(JsonValue value)
    {
        var catalogs = new Dictionary<string, CatalogRegistry>(StringComparer.Ordinal);
        foreach (var (entry, index) in Sequence(value, "document.catalogs").Select((entry, index) => (entry, index)))
        {
            var path = $"document.catalogs[{index}]";
            var fields = Mapping(entry, path);
            Keys(fields, path, ["catalog_id", "versions"]);
            var catalogId = Text(fields.Get("catalog_id"), path + ".catalog_id");
            if (catalogs.ContainsKey(catalogId))
            {
                throw Fail(path, $"duplicate catalog history for {Units.PythonText.Repr(catalogId)}");
            }
            var registry = Model(path, () => new CatalogRegistry(catalogId));
            PublishCatalogVersions(registry, fields.Require("versions"), path);
            catalogs[catalogId] = registry;
        }
        return catalogs;
    }

    private static void PublishCatalogVersions(CatalogRegistry registry, JsonValue value, string parent)
    {
        var versions = Sequence(value, parent + ".versions");
        if (versions.IsEmpty)
        {
            throw Fail(parent + ".versions", "a catalog history needs at least one version");
        }
        for (var index = 0; index < versions.Length; index++)
        {
            var path = $"{parent}.versions[{index}]";
            var fields = Mapping(versions[index], path);
            if (fields.Contains("rollback_to"))
            {
                Keys(fields, path, ["rollback_to", "published_at"], ["effective_at", "note"]);
                var effectiveAt = Optional(fields, "effective_at") is { } effective
                    ? Integer(effective, path + ".effective_at")
                    : (BigInteger?)null;
                Model(path, () => registry.Rollback(Integer(fields.Get("rollback_to"), path + ".rollback_to"),
                    Integer(fields.Get("published_at"), path + ".published_at"), effectiveAt,
                    Text(Default(fields, "note", new JsonString("")), path + ".note")));
                continue;
            }
            Keys(fields, path, ["effective_at", "published_at", "snapshot"], ["note"]);
            Model(path, () => registry.Publish(LoadSnapshot(fields.Require("snapshot"), path + ".snapshot"),
                Integer(fields.Get("effective_at"), path + ".effective_at"),
                Integer(fields.Get("published_at"), path + ".published_at"),
                Text(Default(fields, "note", new JsonString("")), path + ".note")));
        }
    }

    private static CatalogSnapshot LoadSnapshot(JsonValue value, string path)
    {
        var fields = Mapping(value, path);
        Keys(fields, path, [], ["items", "cartons", "pallets", "exclusions", "overrides"]);
        ImmutableArray<T> Entries<T>(string key, Func<JsonObject, string, T> load)
        {
            var entries = Sequence(Default(fields, key, None), $"{path}.{key}");
            return [.. entries.Select((entry, index) => load(Mapping(entry, $"{path}.{key}[{index}]"), $"{path}.{key}[{index}]"))];
        }
        var items = Entries("items", LoadItem);
        var cartons = Entries("cartons", LoadCarton);
        var pallets = Entries("pallets", LoadPallet);
        var exclusions = Entries("exclusions", LoadExclusion);
        var overrides = Entries("overrides", LoadOverride);
        return Model(path, () => new CatalogSnapshot(items, cartons, pallets, exclusions, overrides));
    }

    private static ImmutableArray<BigInteger> Dimensions(JsonValue? value, string path, int axes)
    {
        var entries = Sequence(value, path);
        if (entries.Length != axes)
        {
            throw Fail(path, $"expected exactly {axes} axes");
        }
        return [.. entries.Select((entry, index) => Integer(entry, $"{path}[{index}]"))];
    }

    private static ItemMaster LoadItem(JsonObject fields, string path)
    {
        Keys(fields, path, ["id", "dimensions_mm", "weight_g"], ["description"]);
        return Model(path, () => new ItemMaster(Text(fields.Get("id"), path + ".id"),
            Dimensions(fields.Get("dimensions_mm"), path + ".dimensions_mm", 3),
            Integer(fields.Get("weight_g"), path + ".weight_g"),
            Text(Default(fields, "description", new JsonString("")), path + ".description")));
    }

    private static CartonMaster LoadCarton(JsonObject fields, string path)
    {
        Keys(fields, path, ["id", "inner_dimensions_mm", "max_payload_g"], ["cost_minor"]);
        return Model(path, () => new CartonMaster(Text(fields.Get("id"), path + ".id"),
            Dimensions(fields.Get("inner_dimensions_mm"), path + ".inner_dimensions_mm", 3),
            Integer(fields.Get("max_payload_g"), path + ".max_payload_g"),
            Integer(Default(fields, "cost_minor", new JsonInteger(0)), path + ".cost_minor")));
    }

    private static PalletMaster LoadPallet(JsonObject fields, string path)
    {
        Keys(fields, path, ["id", "deck_dimensions_mm", "max_payload_g"], ["max_stack_height_mm"]);
        var height = Optional(fields, "max_stack_height_mm");
        return Model(path, () => new PalletMaster(Text(fields.Get("id"), path + ".id"),
            Dimensions(fields.Get("deck_dimensions_mm"), path + ".deck_dimensions_mm", 2),
            Integer(fields.Get("max_payload_g"), path + ".max_payload_g"),
            height is null ? null : Integer(height, path + ".max_stack_height_mm")));
    }

    private static ExclusionRule LoadExclusion(JsonObject fields, string path)
    {
        Keys(fields, path, ["id", "scope", "subject_id", "excluded_id"], ["reason"]);
        return Model(path, () => new ExclusionRule(Text(fields.Get("id"), path + ".id"),
            Enum(["item_carton", "item_pallet"], "ExclusionScope", fields.Get("scope"), path + ".scope"),
            Text(fields.Get("subject_id"), path + ".subject_id"),
            Text(fields.Get("excluded_id"), path + ".excluded_id"),
            Text(Default(fields, "reason", new JsonString("")), path + ".reason")));
    }

    private static FacilityOverride LoadOverride(JsonObject fields, string path)
    {
        Keys(fields, path, ["id", "facility_id", "entry_id", "kind", "override"]);
        var kind = Text(fields.Get("kind"), path + ".kind");
        if (kind is not ("item" or "carton" or "pallet"))
        {
            throw Fail(path + ".kind", "expected one of ['carton', 'item', 'pallet']");
        }
        var body = Mapping(fields.Get("override"), path + ".override");
        CatalogEntry entry = kind switch
        {
            "item" => LoadItem(body, path + ".override"),
            "carton" => LoadCarton(body, path + ".override"),
            _ => LoadPallet(body, path + ".override"),
        };
        return Model(path, () => new FacilityOverride(Text(fields.Get("id"), path + ".id"),
            Text(fields.Get("facility_id"), path + ".facility_id"),
            Text(fields.Get("entry_id"), path + ".entry_id"), entry));
    }
}
