using System;
using System.Collections.Immutable;

namespace Packvium.Domain;

/// <summary>Which of an item's own edges lies along the container's x, y and z axes.</summary>
public enum Rotation
{
    LWH,
    LHW,
    WLH,
    WHL,
    HLW,
    HWL,
}

public static class Rotations
{
    /// <summary>Every rotation, in declaration order -- the order the reference engine tries them.</summary>
    public static ImmutableArray<Rotation> All { get; } =
        [Rotation.LWH, Rotation.LHW, Rotation.WLH, Rotation.WHL, Rotation.HLW, Rotation.HWL];

    /// <summary>The rotations that keep the item's own height vertical.</summary>
    public static ImmutableArray<Rotation> Upright { get; } = [Rotation.LWH, Rotation.WLH];

    /// <summary>The public spelling, which is also the enum name.</summary>
    public static string Value(this Rotation rotation) => rotation.ToString();

    public static Rotation Parse(string value) => value switch
    {
        "LWH" => Rotation.LWH,
        "LHW" => Rotation.LHW,
        "WLH" => Rotation.WLH,
        "WHL" => Rotation.WHL,
        "HLW" => Rotation.HLW,
        "HWL" => Rotation.HWL,
        _ => throw new ArgumentException($"'{value}' is not a valid Rotation"),
    };

    /// <summary>Which local axis (0 length, 1 width, 2 height) each container axis takes its extent from.</summary>
    public static (int First, int Second, int Third) SourceAxes(this Rotation rotation) => rotation switch
    {
        Rotation.LWH => (0, 1, 2),
        Rotation.LHW => (0, 2, 1),
        Rotation.WLH => (1, 0, 2),
        Rotation.WHL => (1, 2, 0),
        Rotation.HLW => (2, 0, 1),
        _ => (2, 1, 0),
    };
}
