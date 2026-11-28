using System.Collections.Immutable;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>The parts of the request every per-item check reads.</summary>
/// <param name="Items">The item types by id, in request order.</param>
/// <param name="Containers">The container types by id, in request order.</param>
/// <param name="Clearance">The clearance margin in ticks, on every side of every placement.</param>
/// <param name="ContainerTagRules">The participating <c>require_container_tag</c> policy rules.</param>
internal sealed record RequestContext(Ordered<ItemSpec> Items, Ordered<ContainerSpec> Containers, BigInteger Clearance,
    ImmutableArray<JsonValue> ContainerTagRules);
