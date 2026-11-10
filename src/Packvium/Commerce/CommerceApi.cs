using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;
using Packvium.Units;
using static Packvium.Commerce.CommerceJson;

namespace Packvium.Commerce;

/// <summary>
/// The exported commercial and control-plane API (<c>docs/COMMERCE-API.md</c>): three
/// deterministic functions over one commerce document. Each answers with a result document;
/// a rejection is an answer (<c>status: rejected</c>), and only a malformed input throws
/// <see cref="CommerceInputException"/>. Commerce is a leaf: it shares only the JSON value
/// model with the packing engine and never imports a solver.
/// </summary>
public static class CommerceApi
{
    public const int ApiVersion = 1;

    /// <summary>The closed set of rejection codes, in the order the contract tabulates them.</summary>
    public static readonly ImmutableArray<string> RejectionCodes =
    [
        "tariff_not_found", "no_effective_tariff", "unavailable_zone", "unavailable_accessorial",
        "policy_rule_not_found", "policy_version_not_found", "catalog_not_found", "catalog_version_not_found",
        "no_effective_catalog_version", "ambiguous_catalog_reference",
    ];

    /// <summary>The one byte-comparable spelling of a result: sorted keys, compact separators.</summary>
    public static string CanonicalJson(JsonValue result) => PythonJson.Dumps(result, sortKeys: true);

    /// <summary>Prices one shipment against a pinned or effective-dated tariff version. O(h + a).</summary>
    public static JsonObject Quote(JsonValue document, JsonValue request)
    {
        var loaded = CommerceDocument.Load(document);
        var fields = Mapping(request, "request");
        Keys(fields, "request", ["carrier_id", "service_id", "zone", "actual_weight_g", "volume_mm3"],
            ["tariff_version", "as_of", "requested_accessorials"]);
        var pin = ExactlyOne(fields, "request", ["tariff_version", "as_of"]);
        var carrierId = Text(fields.Get("carrier_id"), "request.carrier_id");
        var serviceId = Text(fields.Get("service_id"), "request.service_id");
        var accessorials = AccessorialIds(fields.GetPresent("requested_accessorials"));
        var ratingRequest = BuildRatingRequest(fields, accessorials);
        try
        {
            var tariff = ResolveTariff(loaded, carrierId, serviceId, fields, pin);
            return Ok("quote", QuotePayload(Rate(tariff, ratingRequest)));
        }
        catch (RejectionException rejection)
        {
            return Rejected(rejection);
        }
    }

    /// <summary>Decides one eligibility question against a pinned or effective-dated rule set. O(r * p).</summary>
    public static JsonObject EvaluatePolicy(JsonValue document, JsonValue request)
    {
        var loaded = CommerceDocument.Load(document);
        var fields = Mapping(request, "request");
        Keys(fields, "request", ["scope", "context"], ["as_of", "rule_versions"]);
        var pin = ExactlyOne(fields, "request", ["as_of", "rule_versions"]);
        var scopeText = Text(fields.Get("scope"), "request.scope");
        if (!PolicyVocabulary.Scopes.Contains(scopeText))
        {
            throw Fail("request.scope", $"unsupported policy scope {PythonText.Repr(scopeText)}");
        }
        var context = fields.Get("context") as JsonObject ?? throw Fail("request.context", "expected an object");
        try
        {
            var decision = pin == "as_of"
                ? loaded.Policies.Evaluate(scopeText, context, Integer(fields.Get("as_of"), "request.as_of"))
                : PolicyRegistry.Decide(PinnedRules(loaded.Policies, fields.Require("rule_versions")), scopeText, context);
            return Ok("decision", DecisionPayload(decision));
        }
        catch (RejectionException rejection)
        {
            return Rejected(rejection);
        }
    }

    /// <summary>Reports which catalog version a reference resolves to and what it contains. O(h + n log n).</summary>
    public static JsonObject CatalogVersionInfo(JsonValue document, JsonValue request)
    {
        var loaded = CommerceDocument.Load(document);
        var fields = Mapping(request, "request");
        Keys(fields, "request", ["catalog_id", "resolved_at"], ["version", "as_of"]);
        var catalogId = Text(fields.Get("catalog_id"), "request.catalog_id");
        var resolvedAt = Integer(fields.Get("resolved_at"), "request.resolved_at");
        var version = fields.GetPresent("version") is { } pinned ? Integer(pinned, "request.version") : (BigInteger?)null;
        var asOf = fields.GetPresent("as_of") is { } instant ? Integer(instant, "request.as_of") : (BigInteger?)null;
        if (version is not null && asOf is not null)
        {
            throw Fail("request", "expected at most one of ['version', 'as_of']");
        }
        try
        {
            var resolved = ResolveCatalog(loaded, catalogId, resolvedAt, version, asOf);
            return Ok("catalog", CatalogPayload(loaded, resolved));
        }
        catch (RejectionException rejection)
        {
            return Rejected(rejection);
        }
    }

    // ------------------------------------------------------------------------------ quote

    /// <summary>A list of strings and nothing else: a bare string or an object is a malformed request.</summary>
    private static ImmutableArray<string> AccessorialIds(JsonValue? value) => value switch
    {
        null => [],
        JsonArray list => [.. list.Items.Select((entry, index) => Text(entry, $"request.requested_accessorials[{index}]"))],
        _ => throw Fail("request.requested_accessorials", "expected a list"),
    };

    private static RatingRequest BuildRatingRequest(JsonObject fields, ImmutableArray<string> accessorials)
    {
        try
        {
            return new RatingRequest(Text(fields.Get("zone"), "request.zone"),
                Integer(fields.Get("actual_weight_g"), "request.actual_weight_g"),
                Integer(fields.Get("volume_mm3"), "request.volume_mm3"), accessorials);
        }
        catch (ArgumentException error)
        {
            throw new CommerceInputException($"request: {error.Message}");
        }
    }

    private static Tariff ResolveTariff(CommerceDocument loaded, string carrierId, string serviceId, JsonObject fields,
        string pin)
    {
        (string, JsonValue)[] identity = [("carrier_id", String(carrierId)), ("service_id", String(serviceId))];
        if (pin == "tariff_version")
        {
            var version = Integer(fields.Get("tariff_version"), "request.tariff_version");
            try
            {
                return loaded.Carriers.Tariff(carrierId, serviceId, version);
            }
            catch (TariffNotFoundException)
            {
                throw Rejection("tariff_not_found", [.. identity, ("tariff_version", Number(version))]);
            }
        }
        var asOf = Integer(fields.Get("as_of"), "request.as_of");
        try
        {
            loaded.Carriers.Versions(carrierId, serviceId);
        }
        catch (TariffNotFoundException)
        {
            throw Rejection("tariff_not_found", identity);
        }
        try
        {
            return loaded.Carriers.EffectiveTariff(carrierId, serviceId, asOf);
        }
        catch (TariffNotFoundException)
        {
            throw Rejection("no_effective_tariff", [.. identity, ("as_of", Number(asOf))]);
        }
    }

    private static RateBreakdown Rate(Tariff tariff, RatingRequest request)
    {
        try
        {
            return Rating.RateTariff(tariff, request);
        }
        catch (UnavailableServiceException error)
        {
            (string, JsonValue)[] identity =
            [
                ("carrier_id", String(tariff.CarrierId)), ("service_id", String(tariff.ServiceId)),
                ("tariff_version", Number(tariff.Version)),
            ];
            throw error.Zone is { } zone
                ? Rejection("unavailable_zone", [.. identity, ("zone", String(zone))])
                : Rejection("unavailable_accessorial", [.. identity,
                    ("accessorial_ids", new JsonArray([.. error.AccessorialIds.Select(id => (JsonValue)String(id))]))]);
        }
    }

    private static JsonObject QuotePayload(RateBreakdown breakdown) => Object(
        ("carrier_id", String(breakdown.CarrierId)),
        ("service_id", String(breakdown.ServiceId)),
        ("tariff_version", Number(breakdown.TariffVersion)),
        ("zone", String(breakdown.Zone)),
        ("actual_weight_g", Number(breakdown.ActualWeightG)),
        ("dimensional_weight_g", Number(breakdown.DimensionalWeightG)),
        ("billed_weight_g", Number(breakdown.BilledWeightG)),
        ("base_charge_minor", Number(breakdown.BaseChargeMinor)),
        ("minimum_charge_applied", new JsonBool(breakdown.MinimumChargeApplied)),
        ("fuel_surcharge_minor", Number(breakdown.FuelSurchargeMinor)),
        ("accessorial_charges_minor", new JsonArray([.. breakdown.AccessorialChargesMinor.Select(charge =>
            (JsonValue)new JsonArray([String(charge.AccessorialId), Number(charge.Amount)]))])),
        ("total_minor", Number(breakdown.TotalMinor)));

    // ----------------------------------------------------------------------------- policy

    private static ImmutableArray<PolicyRule> PinnedRules(PolicyRegistry policies, JsonValue value)
    {
        if (value is not JsonArray list)
        {
            throw Fail("request.rule_versions", "expected a list");
        }
        var pins = new List<(string, BigInteger)>();
        for (var index = 0; index < list.Items.Length; index++)
        {
            var path = $"request.rule_versions[{index}]";
            if (list.Items[index] is not JsonArray { Items.Length: 2 } pair)
            {
                throw Fail(path, "expected a [rule_id, version] pair");
            }
            pins.Add((Text(pair.Items[0], path + "[0]"), Integer(pair.Items[1], path + "[1]")));
        }
        try
        {
            return policies.ResolveVersions(pins);
        }
        catch (ArgumentException error)
        {
            throw new CommerceInputException($"request.rule_versions: {error.Message}");
        }
        catch (PolicyVersionNotFoundException error)
        {
            throw Rejection("policy_version_not_found", [("rule_id", String(error.RuleId)), ("version", Number(error.Version))]);
        }
        catch (PolicyRuleNotFoundException error)
        {
            throw Rejection("policy_rule_not_found", [("rule_id", String(error.RuleId))]);
        }
    }

    private static JsonObject DecisionPayload(PolicyDecision decision) => Object(
        ("scope", String(decision.Scope)),
        ("allowed", new JsonBool(decision.Allowed)),
        ("citation", decision.Citation is not { } rule
            ? JsonNull.Instance
            : Object(("rule_id", String(rule.RuleId)), ("version", Number(rule.Version)), ("action", String(rule.Action)),
                ("priority", Number(rule.Priority)), ("reason", String(rule.Reason)))));

    // ---------------------------------------------------------------------------- catalog

    private static ResolvedCatalog ResolveCatalog(CommerceDocument loaded, string catalogId, BigInteger resolvedAt,
        BigInteger? version, BigInteger? asOf)
    {
        if (!loaded.Catalogs.TryGetValue(catalogId, out var registry))
        {
            throw Rejection("catalog_not_found", [("catalog_id", String(catalogId))]);
        }
        var selector = new List<(string, JsonValue)> { ("catalog_id", String(catalogId)) };
        if (version is { } pinned)
        {
            selector.Add(("version", Number(pinned)));
        }
        if (asOf is { } instant)
        {
            selector.Add(("as_of", Number(instant)));
        }
        try
        {
            return registry.Resolve(resolvedAt, version, asOf);
        }
        catch (CatalogVersionNotFoundException)
        {
            throw Rejection("catalog_version_not_found", [.. selector]);
        }
        catch (NoEffectiveCatalogVersionException)
        {
            throw Rejection("no_effective_catalog_version", [.. selector]);
        }
        catch (AmbiguousCatalogReferenceException)
        {
            throw Rejection("ambiguous_catalog_reference", [.. selector]);
        }
    }

    private static JsonObject CatalogPayload(CommerceDocument loaded, ResolvedCatalog resolved)
    {
        var reference = resolved.Reference;
        var snapshot = resolved.Snapshot;
        var published = loaded.Catalogs[reference.CatalogId].Versions[reference.Version - 1];
        JsonArray Ids(IEnumerable<CatalogEntry> entries) =>
            new([.. entries.Select(entry => entry.Id).Order(CodePointComparer.Instance).Select(id => (JsonValue)String(id))]);
        return Object(
            ("catalog_id", String(reference.CatalogId)),
            ("version", Number(reference.Version)),
            ("effective_at", Number(reference.EffectiveAt)),
            ("published_at", Number(published.PublishedAt)),
            ("resolved_at", Number(reference.ResolvedAt)),
            ("rolled_back_from", published.RolledBackFrom is { } from ? Number(from) : JsonNull.Instance),
            ("note", String(published.Note)),
            ("entry_counts", Object(
                ("items", Number(snapshot.Items.Length)), ("cartons", Number(snapshot.Cartons.Length)),
                ("pallets", Number(snapshot.Pallets.Length)), ("exclusions", Number(snapshot.Exclusions.Length)),
                ("overrides", Number(snapshot.Overrides.Length)))),
            ("item_ids", Ids(snapshot.Items)),
            ("carton_ids", Ids(snapshot.Cartons)),
            ("pallet_ids", Ids(snapshot.Pallets)));
    }

    // -------------------------------------------------------------------------- envelopes

    private static RejectionException Rejection(string code, (string Key, JsonValue Value)[] fields) =>
        new(code, [.. fields.Select(field => new KeyValuePair<string, JsonValue>(field.Key, field.Value))]);

    private static JsonObject Ok(string key, JsonObject payload) =>
        Object(("api_version", Number(ApiVersion)), ("status", String("ok")), (key, payload));

    private static JsonObject Rejected(RejectionException rejection) => Object(
        ("api_version", Number(ApiVersion)),
        ("status", String("rejected")),
        ("error", Object(("code", String(rejection.Code)), ("fields", new JsonObject(rejection.Fields)))));
}
