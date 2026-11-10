using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Packvium.Domain;

/// <summary>One observed reason an item was not placed.</summary>
public sealed record RejectionObservation(string Code, long Count = 1, ImmutableArray<string> Details = default)
{
    public ImmutableArray<string> Details { get; init; } = Details.IsDefault ? [] : Details;
}

/// <summary>How strongly a rejection reason is established, and the observations behind it.</summary>
public sealed record ReasonProof(string Level, ImmutableArray<RejectionObservation> Observations)
{
    /// <summary>Statements about the request that no search outcome can change.</summary>
    private static readonly FrozenSet<string> Proven = new[]
    {
        "no_compatible_container_dimensions", "payload_exceeded", "no_eligible_container", "rotation_restricted",
        "policy_rule",
    }.ToFrozenSet();

    private static readonly FrozenSet<string> LimitBound = new[] { "time_limit", "effort_limit" }.ToFrozenSet();

    private static readonly FrozenSet<string> Observed = new[]
    {
        "no_feasible_placement", "search_exhausted", "exact_search_incomplete", "insufficient_support",
    }.ToFrozenSet();

    public static ReasonProof ForReason(string reason, ImmutableArray<string> details = default)
    {
        var level = Proven.Contains(reason) ? "proven"
            : LimitBound.Contains(reason) ? "unknown_due_to_limit"
            : Observed.Contains(reason) ? "observed"
            : "inferred";
        return new ReasonProof(level, [new RejectionObservation(reason, 1, details)]);
    }
}

/// <summary>An instance left out of every container, with why.</summary>
public sealed class UnpackedItem(ItemInstance instance, string reason, ImmutableArray<string> details = default,
    ReasonProof? proof = null)
{
    public ItemInstance Instance { get; } = instance;

    public string Reason { get; } = reason;

    public ImmutableArray<string> Details { get; } = details.IsDefault ? [] : details;

    public ReasonProof Proof { get; } = proof ?? ReasonProof.ForReason(reason, details);
}

/// <summary>The items and containers of one request, with ids unique within each.</summary>
public sealed class PackingRequest
{
    public PackingRequest(IEnumerable<Item> items, IEnumerable<Container> containers)
    {
        Items = [.. items];
        Containers = [.. containers];
        if (Items.IsEmpty)
        {
            throw new ArgumentException("at least one item is required");
        }
        if (Containers.IsEmpty)
        {
            throw new ArgumentException("at least one container is required");
        }
        if (Items.Select(item => item.Id).Distinct().Count() != Items.Length)
        {
            throw new ArgumentException("item ids must be unique");
        }
        if (Containers.Select(container => container.Id).Distinct().Count() != Containers.Length)
        {
            throw new ArgumentException("container ids must be unique");
        }
    }

    public ImmutableArray<Item> Items { get; }

    public ImmutableArray<Container> Containers { get; }

    public ImmutableArray<ItemInstance> Instances =>
        [.. Items.SelectMany(item => item.Instances())];
}
