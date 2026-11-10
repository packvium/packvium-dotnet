using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using Packvium.Units;

namespace Packvium.Algorithms;

/// <summary>
/// A Python tuple used as a sort or ranking key: compared element by element, integers by
/// value, strings by code point, nested tuples recursively, a shorter prefix first. Every
/// tie-break in the reference search is such a tuple, and reproducing it exactly is what
/// keeps two engines choosing the same placement.
/// </summary>
public sealed class SortKey : IComparable<SortKey>, IEquatable<SortKey>
{
    private readonly ImmutableArray<object> elements;

    private SortKey(ImmutableArray<object> elements) => this.elements = elements;

    public static SortKey Of(params object[] elements)
    {
        var builder = ImmutableArray.CreateBuilder<object>(elements.Length);
        foreach (var element in elements)
        {
            builder.Add(Normalise(element));
        }
        return new SortKey(builder.MoveToImmutable());
    }

    public int Count => elements.Length;

    public object this[int index] => elements[index];

    public static bool operator ==(SortKey? left, SortKey? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(SortKey? left, SortKey? right) => !(left == right);

    public static bool operator <(SortKey left, SortKey right) => left.CompareTo(right) < 0;

    public static bool operator >(SortKey left, SortKey right) => left.CompareTo(right) > 0;

    public static bool operator <=(SortKey left, SortKey right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SortKey left, SortKey right) => left.CompareTo(right) >= 0;

    public int CompareTo(SortKey? other)
    {
        if (other is null)
        {
            return 1;
        }
        var shared = Math.Min(elements.Length, other.elements.Length);
        for (var index = 0; index < shared; index++)
        {
            var order = CompareElements(elements[index], other.elements[index]);
            if (order != 0)
            {
                return order;
            }
        }
        return elements.Length.CompareTo(other.elements.Length);
    }

    public bool Equals(SortKey? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SortKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var element in elements)
        {
            hash.Add(element);
        }
        return hash.ToHashCode();
    }

    private static object Normalise(object element) => element switch
    {
        int value => (BigInteger)value,
        long value => (BigInteger)value,
        Int128 value => (BigInteger)value,
        bool value => value ? BigInteger.One : BigInteger.Zero,
        BigInteger or double or string or SortKey => element,
        _ => throw new ArgumentException($"a sort key cannot hold a {element.GetType().Name}"),
    };

    private static int CompareElements(object left, object right) => (left, right) switch
    {
        (BigInteger a, BigInteger b) => a.CompareTo(b),
        (double a, double b) => a.CompareTo(b),
        (BigInteger a, double b) => -CompareMixed(b, a),
        (double a, BigInteger b) => CompareMixed(a, b),
        (string a, string b) => CodePointComparer.Instance.Compare(a, b),
        (SortKey a, SortKey b) => a.CompareTo(b),
        _ => throw new ArgumentException(
            $"'<' not supported between instances of '{left.GetType().Name}' and '{right.GetType().Name}'"),
    };

    /// <summary>A double against an integer, exactly, as Python compares them.</summary>
    private static int CompareMixed(double value, BigInteger integer)
    {
        if (double.IsPositiveInfinity(value))
        {
            return 1;
        }
        if (double.IsNegativeInfinity(value))
        {
            return -1;
        }
        var floor = new BigInteger(Math.Floor(value));
        var order = floor.CompareTo(integer);
        return order != 0 ? order : (value > Math.Floor(value) ? 1 : 0);
    }
}
