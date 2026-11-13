using System.Collections.Generic;
using System.Linq;
using Packvium.Domain;

namespace Packvium.Constraints;

/// <summary>
/// Which built-in rules could reject some candidate of one item in one container. A candidate
/// search evaluates the chain once per feasible position, and most rules are opt-in: they read
/// a flag on the item, the container or the search state and allow when it is off. Those flags
/// are fixed for one search, so the answer is read once instead of once per position. A dropped
/// rule would have allowed every position, so the first rejecting rule is unchanged.
/// <para>
/// Only the sealed built-ins are recognised; a custom constraint never matches and is always
/// evaluated. Internal so the optimisation adds nothing to the public constraint contract.
/// O(c) for c constraints.
/// </para>
/// </summary>
internal static class ConstraintActivity
{
    public static List<IPlacementConstraint> Active(IReadOnlyList<IPlacementConstraint> constraints,
        Container container, ItemInstance item, bool stackSensitive, bool routeSensitive) =>
        [.. constraints.Where(constraint => !IsInert(constraint, container, item.Item, stackSensitive, routeSensitive))];

    private static bool IsInert(IPlacementConstraint constraint, Container container, Item item,
        bool stackSensitive, bool routeSensitive) => constraint switch
    {
        FloorConstraint => !item.MustBeOnFloor,
        ContainerEligibilityConstraint => item.EligibleContainerTags.Count == 0
                                          || item.EligibleContainerTags.Overlaps(container.Tags),
        CompatibilityConstraint => item.Tags.Count == 0 && item.IncompatibleTags.Count == 0,
        TagCountConstraint => container.TagLimits.IsEmpty
                              || !container.TagLimits.Any(limit => item.Tags.Contains(limit.Key)),
        TopLoadConstraint => !stackSensitive,
        RouteOrderConstraint => !routeSensitive,
        StopAccessibilityConstraint stop => !routeSensitive || stop.DirectionsFor(container).IsEmpty,
        AxleLoadConstraint => container.Axles is null,
        _ => false,
    };
}
