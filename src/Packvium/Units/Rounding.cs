namespace Packvium.Units;

/// <summary>How a quantity that falls between two ticks becomes one.</summary>
public enum Rounding
{
    Floor,
    Ceil,
    /// <summary>Nearest tick, ties to even.</summary>
    Nearest,
}
