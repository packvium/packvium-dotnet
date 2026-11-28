namespace Packvium.Domain;

/// <summary>
/// An item already in a known place before the solve: loaded, or locked there. Addressed by
/// container type and instance rather than by a result's container index, which does not exist
/// until the solve finishes. <see cref="Position"/> is the physical origin, as a result reports
/// it, so a result placement can be fixed by quoting it.
/// </summary>
public sealed record FixedPlacement
{
    /// <summary>
    /// Every rule on these fields is checked where the placement sits in a request, by
    /// <see cref="Packing.Packer"/>, with the JSON path's refusal and pointer.
    /// </summary>
    public FixedPlacement(string itemId, string containerId, Point position, Rotation rotation, long containerInstance = 1)
    {
        ItemId = itemId;
        ContainerId = containerId;
        Position = position;
        Rotation = rotation;
        ContainerInstance = containerInstance;
    }

    public string ItemId { get; }

    public string ContainerId { get; }

    public Point Position { get; }

    public Rotation Rotation { get; }

    public long ContainerInstance { get; }

    /// <summary>The id the packed container holding this placement carries: <c>box#1</c>.</summary>
    public string PackedContainerId => $"{ContainerId}#{ContainerInstance}";
}
