using System;

namespace Packvium.Domain;

/// <summary>
/// How much of an item's declared box the item actually occupies. Never inferred: packing a hull
/// as its bounding box would return a plan that validates and does not physically fit.
/// </summary>
public enum ShapeType
{
    RigidCuboid,
    ConvexHull,
    Compressible,
}

public static class ShapeTypes
{
    public static string Value(this ShapeType shapeType) => shapeType switch
    {
        ShapeType.RigidCuboid => "rigid_cuboid",
        ShapeType.ConvexHull => "convex_hull",
        _ => "compressible",
    };

    public static ShapeType Parse(string value) => value switch
    {
        "rigid_cuboid" => ShapeType.RigidCuboid,
        "convex_hull" => ShapeType.ConvexHull,
        "compressible" => ShapeType.Compressible,
        _ => throw new ArgumentException($"'{value}' is not a valid ShapeType"),
    };
}
