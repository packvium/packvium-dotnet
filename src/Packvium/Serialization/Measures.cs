using System;
using Packvium.Domain;
using Packvium.Units;
using DomainDimensions = Packvium.Domain.Dimensions;

namespace Packvium.Serialization;

/// <summary>
/// <c>Length.parse</c> / <c>Weight.parse</c> over a raw JSON measure: an integer, a string that may
/// carry its own unit, or <c>{"value": ..., "unit": ...}</c>. A bare float is refused unless it
/// is integral, as the reference engine refuses it.
/// </summary>
internal static class Measures
{
    /// <summary>A length, refused with the schema's reason when the caller's value is not one.</summary>
    public static Length Length(JsonValue raw, string defaultUnit)
    {
        try
        {
            return ReadLength(raw, defaultUnit);
        }
        catch (UnitRefusal refusal)
        {
            throw new ObjectRefusal(refusal.Reason, refusal.Detail);
        }
    }

    /// <summary>A weight, refused with the schema's reason when the caller's value is not one.</summary>
    public static Weight Weight(JsonValue raw, string defaultUnit = "g")
    {
        try
        {
            return ReadWeight(raw, defaultUnit);
        }
        catch (UnitRefusal refusal)
        {
            throw new ObjectRefusal(refusal.Reason, refusal.Detail);
        }
    }

    private static Length ReadLength(JsonValue raw, string defaultUnit) => raw switch
    {
        JsonObject measure => Units.Length.Of(InnerValue(measure), MeasureUnit(measure, defaultUnit)),
        // A fraction in binary floating point is not the decimal the caller wrote; an integral
        // float is exact, and JavaScript cannot send anything else for `100`.
        JsonFloat number when !double.IsInteger(number.Value) =>
            throw new UnitRefusal("wrong_type", "float length input is intentionally rejected; use a decimal string"),
        JsonFloat number => Units.Length.Of(new System.Numerics.BigInteger(number.Value), defaultUnit),
        JsonString text => Units.Length.Parse(text.Value, defaultUnit),
        _ => Units.Length.Of(InnerValue(raw), defaultUnit),
    };

    private static Weight ReadWeight(JsonValue raw, string defaultUnit) => raw switch
    {
        JsonObject measure => Units.Weight.Of(InnerValue(measure), MeasureUnit(measure, defaultUnit)),
        JsonFloat number when !double.IsInteger(number.Value) =>
            throw new UnitRefusal("wrong_type", "float weight input is intentionally rejected; use a decimal string"),
        JsonFloat number => Units.Weight.Of(Rational.Of(new System.Numerics.BigInteger(number.Value)), defaultUnit),
        JsonString text => Units.Weight.Parse(text.Value, defaultUnit),
        _ => Units.Weight.Of(InnerValue(raw), defaultUnit),
    };

    public static DomainDimensions Dimensions(JsonValue raw, string unit)
    {
        var data = PythonCoercion.Object(raw);
        return new DomainDimensions(Length(data.Require("length"), unit), Length(data.Require("width"), unit),
            Length(data.Require("height"), unit));
    }

    /// <summary>The exact quantity a measure holds, read as <see cref="QuantityText"/> reads text.</summary>
    private static Rational InnerValue(JsonValue raw) => raw switch
    {
        JsonObject measure => InnerScalar(measure.Require("value")),
        _ => InnerScalar(raw),
    };

    private static Rational InnerScalar(JsonValue value) => value switch
    {
        JsonInteger integer => Rational.Of(integer.Value),
        // True is an int to Python and 1 to arithmetic, but never a measure anyone wrote.
        JsonBool => throw new UnitRefusal("wrong_type", "a boolean is not a measure"),
        JsonString text => QuantityText.Parse(text.Value),
        JsonFloat number when !double.IsInteger(number.Value) =>
            throw new UnitRefusal("wrong_type", "float measure input is intentionally rejected; use a decimal string"),
        JsonFloat number => Rational.Of(new System.Numerics.BigInteger(number.Value)),
        // str(None), str([...]) and str({...}) never parse as a Decimal.
        _ => throw new UnitRefusal("wrong_type", $"invalid decimal or fraction: {PythonRepr.Str(value)}"),
    };

    private static string MeasureUnit(JsonObject measure, string defaultUnit) => measure.Get("unit") switch
    {
        null => defaultUnit,
        JsonString unit => unit.Value,
        var other => throw new ArgumentException($"'{PythonCoercion.TypeName(other)}' object has no attribute 'lower'"),
    };
}
