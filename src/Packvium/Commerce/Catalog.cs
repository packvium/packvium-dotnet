using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Units;

namespace Packvium.Commerce;

/// <summary>Base of every catalog-domain error.</summary>
public class CatalogException(string message) : Exception(message);

public sealed class CatalogVersionNotFoundException(string message) : CatalogException(message);

public sealed class NoEffectiveCatalogVersionException(string message) : CatalogException(message);

/// <summary>Neither a version nor an instant while more than one version exists: "current" is undefined.</summary>
public sealed class AmbiguousCatalogReferenceException(string message) : CatalogException(message);

/// <summary>A master-data record with an id; items, cartons and pallets.</summary>
public abstract record CatalogEntry(string Id)
{
    protected static void RequireId(string label, string id)
    {
        if (id.Length == 0)
        {
            throw new ArgumentException($"{label} id is required");
        }
    }

    protected static void RequirePositive(string label, ImmutableArray<BigInteger> dimensions)
    {
        if (dimensions.Any(dimension => dimension <= 0))
        {
            throw new ArgumentException($"{label} dimensions must be positive");
        }
    }
}

public sealed record ItemMaster : CatalogEntry
{
    public ItemMaster(string id, ImmutableArray<BigInteger> dimensionsMm, BigInteger weightG, string description) : base(id)
    {
        RequireId("item", id);
        RequirePositive("item", dimensionsMm);
        if (weightG <= 0)
        {
            throw new ArgumentException("item weight must be positive");
        }
        (DimensionsMm, WeightG, Description) = (dimensionsMm, weightG, description);
    }

    public ImmutableArray<BigInteger> DimensionsMm { get; }

    public BigInteger WeightG { get; }

    public string Description { get; }
}

public sealed record CartonMaster : CatalogEntry
{
    public CartonMaster(string id, ImmutableArray<BigInteger> innerDimensionsMm, BigInteger maxPayloadG, BigInteger costMinor)
        : base(id)
    {
        RequireId("carton", id);
        RequirePositive("carton", innerDimensionsMm);
        if (maxPayloadG <= 0)
        {
            throw new ArgumentException("carton max_payload_g must be positive");
        }
        if (costMinor < 0)
        {
            throw new ArgumentException("cost_minor cannot be negative");
        }
        (InnerDimensionsMm, MaxPayloadG, CostMinor) = (innerDimensionsMm, maxPayloadG, costMinor);
    }

    public ImmutableArray<BigInteger> InnerDimensionsMm { get; }

    public BigInteger MaxPayloadG { get; }

    public BigInteger CostMinor { get; }
}

public sealed record PalletMaster : CatalogEntry
{
    public PalletMaster(string id, ImmutableArray<BigInteger> deckDimensionsMm, BigInteger maxPayloadG,
        BigInteger? maxStackHeightMm) : base(id)
    {
        RequireId("pallet", id);
        RequirePositive("pallet", deckDimensionsMm);
        if (maxPayloadG <= 0)
        {
            throw new ArgumentException("pallet max_payload_g must be positive");
        }
        if (maxStackHeightMm <= 0)
        {
            throw new ArgumentException("max_stack_height_mm must be positive");
        }
        (DeckDimensionsMm, MaxPayloadG, MaxStackHeightMm) = (deckDimensionsMm, maxPayloadG, maxStackHeightMm);
    }

    public ImmutableArray<BigInteger> DeckDimensionsMm { get; }

    public BigInteger MaxPayloadG { get; }

    public BigInteger? MaxStackHeightMm { get; }
}

/// <summary>Forbids one entry from being packed with another, e.g. a hazmat SKU from a carton type.</summary>
public sealed record ExclusionRule : CatalogEntry
{
    public ExclusionRule(string id, string scope, string subjectId, string excludedId, string reason) : base(id)
    {
        RequireId("exclusion", id);
        if (subjectId.Length == 0 || excludedId.Length == 0)
        {
            throw new ArgumentException("an exclusion rule must reference both a subject and an excluded id");
        }
        (Scope, SubjectId, ExcludedId, Reason) = (scope, subjectId, excludedId, reason);
    }

    public string Scope { get; }

    public string SubjectId { get; }

    public string ExcludedId { get; }

    public string Reason { get; }
}

/// <summary>A facility-specific replacement of a base entry; the entry kind is the override's own type.</summary>
public sealed record FacilityOverride : CatalogEntry
{
    public FacilityOverride(string id, string facilityId, string entryId, CatalogEntry @override) : base(id)
    {
        RequireId("facility override", id);
        if (facilityId.Length == 0)
        {
            throw new ArgumentException("facility_id is required");
        }
        if (@override.Id != entryId)
        {
            throw new ArgumentException("a facility override's entry_id must match override.id");
        }
        (FacilityId, EntryId, Override) = (facilityId, entryId, @override);
    }

    public string FacilityId { get; }

    public string EntryId { get; }

    public CatalogEntry Override { get; }
}

/// <summary>The complete immutable content of one catalog version; ids are unique per kind.</summary>
public sealed record CatalogSnapshot
{
    public CatalogSnapshot(ImmutableArray<ItemMaster> items, ImmutableArray<CartonMaster> cartons,
        ImmutableArray<PalletMaster> pallets, ImmutableArray<ExclusionRule> exclusions,
        ImmutableArray<FacilityOverride> overrides)
    {
        RequireUnique("item", items);
        RequireUnique("carton", cartons);
        RequireUnique("pallet", pallets);
        RequireUnique("exclusion", exclusions);
        RequireUnique("facility override", overrides);
        (Items, Cartons, Pallets, Exclusions, Overrides) = (items, cartons, pallets, exclusions, overrides);
    }

    public ImmutableArray<ItemMaster> Items { get; }

    public ImmutableArray<CartonMaster> Cartons { get; }

    public ImmutableArray<PalletMaster> Pallets { get; }

    public ImmutableArray<ExclusionRule> Exclusions { get; }

    public ImmutableArray<FacilityOverride> Overrides { get; }

    private static void RequireUnique<T>(string label, ImmutableArray<T> entries) where T : CatalogEntry
    {
        if (entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
        {
            throw new ArgumentException($"duplicate {label} ids in catalog snapshot");
        }
    }
}

/// <summary>One numbered entry of a catalog's append-only history; a rollback is a new, higher version.</summary>
public sealed record CatalogVersion(int Number, CatalogSnapshot Snapshot, BigInteger EffectiveAt, BigInteger PublishedAt,
    int? RolledBackFrom, string Note);

public sealed record CatalogReference(string CatalogId, int Version, BigInteger EffectiveAt, BigInteger ResolvedAt);

public sealed record ResolvedCatalog(CatalogReference Reference, CatalogSnapshot Snapshot);

/// <summary>
/// Per-catalog append-only publication history: the one place catalog state changes, and only
/// by appending. Resolution is O(h) in the history length.
/// </summary>
public sealed class CatalogRegistry
{
    private readonly List<CatalogVersion> versions = [];

    public CatalogRegistry(string catalogId)
    {
        if (catalogId.Length == 0)
        {
            throw new ArgumentException("catalog_id is required");
        }
        CatalogId = catalogId;
    }

    public string CatalogId { get; }

    public IReadOnlyList<CatalogVersion> Versions => versions;

    public CatalogVersion Publish(CatalogSnapshot snapshot, BigInteger effectiveAt, BigInteger publishedAt, string note) =>
        Append(snapshot, effectiveAt, publishedAt, null, note);

    /// <summary>A new version whose snapshot equals an earlier one's; history is never rewritten.</summary>
    public CatalogVersion Rollback(BigInteger toVersion, BigInteger publishedAt, BigInteger? effectiveAt, string note)
    {
        var target = Version(toVersion);
        return Append(target.Snapshot, effectiveAt ?? publishedAt, publishedAt, target.Number,
            note.Length > 0 ? note : $"rollback to version {toVersion}");
    }

    public ResolvedCatalog Resolve(BigInteger resolvedAt, BigInteger? version, BigInteger? asOf)
    {
        var target = ResolveVersion(version, asOf);
        if (resolvedAt < 0)
        {
            throw new InvalidOperationException("ValueError: resolved_at cannot be negative");
        }
        return new ResolvedCatalog(new CatalogReference(CatalogId, target.Number, target.EffectiveAt, resolvedAt),
            target.Snapshot);
    }

    private CatalogVersion Append(CatalogSnapshot snapshot, BigInteger effectiveAt, BigInteger publishedAt,
        int? rolledBackFrom, string note)
    {
        if (effectiveAt < 0)
        {
            throw new ArgumentException("effective_at cannot be negative");
        }
        if (publishedAt < 0)
        {
            throw new ArgumentException("published_at cannot be negative");
        }
        var version = new CatalogVersion(versions.Count + 1, snapshot, effectiveAt, publishedAt, rolledBackFrom, note);
        versions.Add(version);
        return version;
    }

    private CatalogVersion ResolveVersion(BigInteger? version, BigInteger? asOf)
    {
        if (version is { } number)
        {
            return Version(number);
        }
        if (asOf is not { } instant)
        {
            if (versions.Count > 1)
            {
                throw new AmbiguousCatalogReferenceException(
                    $"catalog {PythonText.Repr(CatalogId)} has {versions.Count} versions; resolve() requires an explicit "
                    + "version or as_of to avoid an ambiguous 'current'");
            }
            return versions.Count == 1
                ? versions[0]
                : throw new CatalogVersionNotFoundException($"catalog {PythonText.Repr(CatalogId)} has no published versions");
        }
        // A same-instant correction or rollback wins over what it corrects: ties go to the higher number.
        return versions.Where(candidate => candidate.EffectiveAt <= instant)
                   .OrderByDescending(candidate => candidate.EffectiveAt).ThenByDescending(candidate => candidate.Number)
                   .FirstOrDefault()
               ?? throw new NoEffectiveCatalogVersionException(
                   $"catalog {PythonText.Repr(CatalogId)} has no version effective as of {instant}");
    }

    private CatalogVersion Version(BigInteger number) =>
        versions.FirstOrDefault(candidate => candidate.Number == number)
        ?? throw new CatalogVersionNotFoundException($"catalog {PythonText.Repr(CatalogId)} has no version {number}");
}
