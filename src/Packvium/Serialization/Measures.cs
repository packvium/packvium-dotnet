using System;
using Packvium.Units;
using DomainDimensions = Packvium.Domain.Dimensions;

namespace Packvium.Serialization;

/// <summary>
/// <c>Length.parse</c> / <c>Weight.parse</c> over a raw JSON measure: an integer, a string that may
/// carry its own unit, or <c>{"value": ..., "unit": ...}</c>. A bare float is refused, as the
/// reference engine refuses it.
/// </summary>
internal static class Measures
{
    public static Length Length(JsonValue raw, string defaultUnit) => raw switch
    {
        JsonObject measure => Units.Length.Of(InnerValue(measure), MeasureUnit(measure, defaultUnit)),
        JsonFloat => throw new ArgumentException("float length input is intentionally rejected; use a decimal string"),
        JsonString text => Units.Length.Parse(text.Value, defaultUnit),
        _ => Units.Length.Of(InnerValue(raw), defaultUnit),
    };

    public static Weight Weight(JsonValue raw, string defaultUnit = "g") => raw switch
    {
        JsonObject measure => Units.Weight.Of(InnerValue(measure), MeasureUnit(measure, defaultUnit)),
        JsonFloat => throw new ArgumentException("float weight input is intentionally rejected; use a decimal string"),
        JsonString text => Units.Weight.Parse(text.Value, defaultUnit),
        _ => Units.Weight.Of(InnerValue(raw), defaultUnit),
    };

    public static DomainDimensions Dimensions(JsonValue raw, string unit)
    {
        var data = PythonCoercion.Object(raw);
        return new DomainDimensions(Length(data.Require("length"), unit), Length(data.Require("width"), unit),
            Length(data.Require("height"), unit));
    }

    /// <summary><c>units._fraction</c> over whatever the measure holds.</summary>
    private static Rational InnerValue(JsonValue raw) => raw switch
    {
        JsonObject measure => InnerScalar(measure.Require("value")),
        _ => InnerScalar(raw),
    };

    private static Rational InnerScalar(JsonValue value) => value switch
    {
        JsonInteger integer => Rational.Of(integer.Value),
        JsonBool boolean => Rational.Of(boolean.Value ? 1 : 0),
        JsonString text => QuantityText.Parse(text.Value),
        JsonFloat number => QuantityText.Parse(PythonFloat.Repr(number.Value)),
        // str(None), str([...]) and str({...}) never parse as a Decimal.
        _ => throw new ArithmeticException("[<class 'decimal.ConversionSyntax'>]"),
    };

    private static string MeasureUnit(JsonObject measure, string defaultUnit) => measure.Get("unit") switch
    {
        null => defaultUnit,
        JsonString unit => unit.Value,
        var other => throw new ArgumentException($"'{PythonCoercion.TypeName(other)}' object has no attribute 'lower'"),
    };
}
