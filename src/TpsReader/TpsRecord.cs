using System.Globalization;
using TpsReader.Internal;

namespace TpsReader;

/// <summary>Represents one materialized TPS table record.</summary>
public sealed class TpsRecord
{
    private static readonly IReadOnlySet<string> EmptyAliases =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, TpsFieldType> EmptyFieldTypes =
        new Dictionary<string, TpsFieldType>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, object?> EmptyValues =
        new Dictionary<string, object?>();
    private static readonly IReadOnlyDictionary<string, TpsMemoValue> EmptyMemos =
        new Dictionary<string, TpsMemoValue>();

    private readonly IReadOnlyDictionary<string, object?> _valuesByName;
    private readonly IReadOnlyDictionary<string, TpsMemoValue> _memosByName;
    private readonly IReadOnlySet<string> _ambiguousFieldAliases;
    private readonly IReadOnlySet<string> _ambiguousMemoAliases;
    private readonly IReadOnlyDictionary<string, TpsFieldType> _fieldTypesByName;
    private readonly TpsRecordLayout? _layout;
    private readonly object?[]? _values;
    private readonly TpsMemoValue[]? _memos;

    internal TpsRecord(
        int recordNumber,
        IReadOnlyDictionary<string, object?> valuesByName,
        IReadOnlyDictionary<string, TpsMemoValue> memosByName,
        IReadOnlySet<string>? ambiguousFieldAliases = null,
        IReadOnlySet<string>? ambiguousMemoAliases = null,
        IReadOnlyDictionary<string, TpsFieldType>? fieldTypesByName = null,
        int sourcePageOffset = 0)
    {
        RecordNumber = recordNumber;
        _valuesByName = valuesByName;
        _memosByName = memosByName;
        _ambiguousFieldAliases = ambiguousFieldAliases ?? EmptyAliases;
        _ambiguousMemoAliases = ambiguousMemoAliases ?? EmptyAliases;
        _fieldTypesByName = fieldTypesByName ?? EmptyFieldTypes;
        SourcePageOffset = sourcePageOffset;
    }

    internal TpsRecord(
        int recordNumber,
        object?[] values,
        TpsMemoValue[] memos,
        TpsRecordLayout layout,
        int sourcePageOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(memos);
        ArgumentNullException.ThrowIfNull(layout);
        if (values.Length != layout.FieldCount || memos.Length != layout.MemoCount)
        {
            throw new ArgumentException("TPS record values do not match their shared table layout.");
        }

        RecordNumber = recordNumber;
        _values = values;
        _memos = memos;
        _layout = layout;
        _valuesByName = EmptyValues;
        _memosByName = EmptyMemos;
        _ambiguousFieldAliases = EmptyAliases;
        _ambiguousMemoAliases = EmptyAliases;
        _fieldTypesByName = EmptyFieldTypes;
        SourcePageOffset = sourcePageOffset;
    }

    /// <summary>Gets the record number stored by TPS.</summary>
    public int RecordNumber { get; }

    /// <summary>Gets the byte offset of the source page containing this record.</summary>
    public int SourcePageOffset { get; }

    /// <summary>Gets a field, MEMO, or BLOB by full name or unambiguous short name.</summary>
    public object? this[string name] => GetValue(name);

    /// <summary>Gets a field, MEMO, or BLOB in its default .NET representation.</summary>
    public object? GetValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ToPublicValue(ResolveValue(name).Value);
    }

    /// <summary>Gets and converts a scalar or complete typed array value.</summary>
    public T? Get<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var resolved = ResolveValue(name);
        return (T?)ConvertValue(resolved.Value, typeof(T), name, resolved.FieldType);
    }

    /// <summary>Gets and converts one zero-based element of an array field.</summary>
    public T? Get<T>(string name, int elementIndex)
    {
        ValidateElementIndex(elementIndex);
        var resolved = GetSingleValue(name, elementIndex);
        return (T?)ConvertValue(resolved.Value, typeof(T), name, resolved.FieldType);
    }

    /// <summary>Attempts to get and convert a scalar or complete typed array value.</summary>
    public bool TryGet<T>(string name, out T? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            value = Get<T>(name);
            return true;
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            value = default;
            return false;
        }
    }

    /// <summary>Attempts to get and convert one zero-based element of an array field.</summary>
    public bool TryGet<T>(string name, int elementIndex, out T? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateElementIndex(elementIndex);
        try
        {
            value = Get<T>(name, elementIndex);
            return true;
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            value = default;
            return false;
        }
    }

    /// <summary>Gets a string, MEMO, or GROUP text value.</summary>
    public string? GetString(string name, int elementIndex = 0)
    {
        var resolved = GetSingleValue(name, elementIndex);
        return ConvertValue(resolved.Value, typeof(string), name, resolved.FieldType) as string;
    }

    /// <summary>Gets an integer value representable by <see cref="int"/>.</summary>
    public int GetInt32(string name, int elementIndex = 0) => GetSingleValue(name, elementIndex).Value switch
    {
        byte value => value,
        short value => value,
        ushort value => value,
        int value => value,
        long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
        var value => throw WrongType(name, "Int32", value)
    };

    /// <summary>Gets a nonnegative integer value representable by <see cref="uint"/>.</summary>
    public uint GetUInt32(string name, int elementIndex = 0) => GetSingleValue(name, elementIndex).Value switch
    {
        byte value => value,
        short value when value >= 0 => (uint)value,
        ushort value => value,
        int value when value >= 0 => (uint)value,
        long value when value is >= 0 and <= uint.MaxValue => (uint)value,
        var value => throw WrongType(name, "UInt32", value)
    };

    /// <summary>Gets a floating-point value, including a DECIMAL string when it can be parsed.</summary>
    public double GetDouble(string name, int elementIndex = 0) => GetSingleValue(name, elementIndex).Value switch
    {
        byte value => value,
        short value => value,
        ushort value => value,
        int value => value,
        long value => value,
        float value => value,
        double value => value,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        var value => throw WrongType(name, "Double", value)
    };

    /// <summary>Gets a nullable date value.</summary>
    public DateOnly? GetDate(string name, int elementIndex = 0) => GetSingleValue(name, elementIndex).Value switch
    {
        null => null,
        DateOnly date => date,
        var value => throw WrongType(name, "DateOnly", value)
    };

    /// <summary>Gets a nullable time value.</summary>
    public TimeOnly? GetTime(string name, int elementIndex = 0) => GetSingleValue(name, elementIndex).Value switch
    {
        null => null,
        TimeOnly time => time,
        var value => throw WrongType(name, "TimeOnly", value)
    };

    /// <summary>Gets cloned bytes from a binary field, BLOB, or raw GROUP element.</summary>
    public byte[]? GetBytes(string name, int elementIndex = 0)
    {
        var resolved = GetSingleValue(name, elementIndex);
        return ConvertValue(resolved.Value, typeof(byte[]), name, resolved.FieldType) as byte[];
    }

    /// <summary>Gets text from a declared MEMO.</summary>
    public string? GetMemo(string name)
    {
        var memo = GetMemoValue(name);
        if (!memo.Definition.IsMemo)
        {
            throw new TpsParseException(new TpsParseError($"Memo '{name}' is a BLOB, not a MEMO."));
        }

        return memo.Text;
    }

    /// <summary>Gets a cloned byte array from a declared BLOB.</summary>
    public byte[]? GetBlob(string name)
    {
        var memo = GetMemoValue(name);
        if (!memo.Definition.IsBlob)
        {
            throw new TpsParseException(new TpsParseError($"Memo '{name}' is a MEMO, not a BLOB."));
        }

        return memo.Blob?.ToArray();
    }

    /// <summary>Gets the availability and integrity state of a MEMO or BLOB.</summary>
    public TpsMemoState GetMemoState(string name) => GetMemoValue(name).State;

    /// <summary>Gets the lossless string representation of a DECIMAL value.</summary>
    public string GetDecimalString(string name, int elementIndex = 0)
    {
        var resolved = GetSingleValue(name, elementIndex);
        return resolved.Value as string ?? throw WrongType(name, "decimal string", resolved.Value);
    }

    /// <summary>Attempts to convert a DECIMAL string to <see cref="decimal"/>.</summary>
    public bool TryGetDecimal(string name, out decimal value, int elementIndex = 0)
    {
        var raw = GetSingleValue(name, elementIndex).Value;
        if (raw is string text && TryParseExactDecimal(text, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private ResolvedValue GetSingleValue(string name, int elementIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateElementIndex(elementIndex);
        var resolved = ResolveValue(name);
        if (resolved.Value is object?[] values)
        {
            if ((uint)elementIndex >= (uint)values.Length)
            {
                throw new TpsParseException(new TpsParseError(
                    $"Element index {elementIndex} is outside field '{name}' with {values.Length} elements."));
            }

            return resolved with { Value = values[elementIndex] };
        }

        if (elementIndex != 0)
        {
            throw new TpsParseException(new TpsParseError(
                $"Element index {elementIndex} is invalid because field '{name}' is not an array."));
        }

        return resolved;
    }

    private ResolvedValue ResolveValue(string name)
    {
        if (_layout is not null)
        {
            var layoutHasField = _layout.TryGetField(name, out var fieldIndex);
            var layoutHasMemo = _layout.TryGetMemo(name, out var memoIndex);
            if (layoutHasField && layoutHasMemo)
            {
                throw new TpsParseException(new TpsParseError(
                    $"Name '{name}' is ambiguous between a field and a MEMO/BLOB; use a table-qualified name."));
            }

            if (_layout.IsAmbiguous(name))
            {
                throw new TpsParseException(new TpsParseError(
                    $"Name '{name}' is ambiguous; use its full table-qualified name."));
            }

            if (layoutHasField)
            {
                var fieldType = _layout.GetFieldType(fieldIndex);
                return new ResolvedValue(
                    _values![fieldIndex],
                    fieldType == TpsFieldType.Unknown ? null : fieldType);
            }

            if (layoutHasMemo)
            {
                var memo = _memos![memoIndex];
                return new ResolvedValue(memo, memo.Definition.Type);
            }

            throw new TpsParseException(new TpsParseError(
                $"Field, MEMO, or BLOB '{name}' was not found in record {RecordNumber}."));
        }

        var hasField = _valuesByName.TryGetValue(name, out var fieldValue);
        var hasMemo = _memosByName.TryGetValue(name, out var memoValue);
        if (hasField && hasMemo)
        {
            throw new TpsParseException(new TpsParseError(
                $"Name '{name}' is ambiguous between a field and a MEMO/BLOB; use a table-qualified name."));
        }

        if (_ambiguousFieldAliases.Contains(name) || _ambiguousMemoAliases.Contains(name))
        {
            throw new TpsParseException(new TpsParseError(
                $"Name '{name}' is ambiguous; use its full table-qualified name."));
        }

        if (hasField)
        {
            _fieldTypesByName.TryGetValue(name, out var fieldType);
            return new ResolvedValue(fieldValue, fieldType == TpsFieldType.Unknown ? null : fieldType);
        }

        if (hasMemo)
        {
            return new ResolvedValue(memoValue, memoValue!.Definition.Type);
        }

        throw new TpsParseException(new TpsParseError(
            $"Field, MEMO, or BLOB '{name}' was not found in record {RecordNumber}."));
    }

    private TpsMemoValue GetMemoValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_layout is not null)
        {
            if (_layout.TryGetMemo(name, out var memoIndex))
            {
                return _memos![memoIndex];
            }

            if (_layout.IsMemoAmbiguous(name))
            {
                throw new TpsParseException(new TpsParseError(
                    $"MEMO/BLOB name '{name}' is ambiguous; use its full table-qualified name."));
            }

            throw new TpsParseException(new TpsParseError(
                $"Memo/BLOB '{name}' was not found in record {RecordNumber}."));
        }

        if (_memosByName.TryGetValue(name, out var value))
        {
            return value;
        }

        if (_ambiguousMemoAliases.Contains(name))
        {
            throw new TpsParseException(new TpsParseError(
                $"MEMO/BLOB name '{name}' is ambiguous; use its full table-qualified name."));
        }

        throw new TpsParseException(new TpsParseError(
            $"Memo/BLOB '{name}' was not found in record {RecordNumber}."));
    }

    private static object? ConvertValue(object? value, Type requestedType, string name, TpsFieldType? fieldType)
    {
        var targetType = Nullable.GetUnderlyingType(requestedType) ?? requestedType;
        if (value is null || value is TpsMemoValue { Definition.IsMemo: true, Text: null } ||
            value is TpsMemoValue { Definition.IsBlob: true, Blob: null })
        {
            if (!requestedType.IsValueType || Nullable.GetUnderlyingType(requestedType) is not null)
            {
                return null;
            }

            throw WrongType(name, requestedType.Name, null);
        }

        if (value is object?[] elements)
        {
            return ConvertArray(elements, targetType, name, fieldType);
        }

        if (value is TpsGroupValue group)
        {
            value = targetType == typeof(byte[]) ? group.CopyRawBytes() : group.Text;
        }
        else if (value is TpsMemoValue memo)
        {
            value = memo.Definition.IsMemo ? memo.Text : memo.Blob?.ToArray();
        }

        if (value is null)
        {
            if (!requestedType.IsValueType || Nullable.GetUnderlyingType(requestedType) is not null)
            {
                return null;
            }

            throw WrongType(name, requestedType.Name, null);
        }

        if (value is byte[] bytes)
        {
            var copy = bytes.ToArray();
            if (targetType.IsInstanceOfType(copy))
            {
                return copy;
            }

            throw WrongType(name, requestedType.Name, value);
        }

        if (targetType == typeof(object))
        {
            return value;
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (targetType == typeof(decimal) && fieldType == TpsFieldType.Decimal && value is string decimalText)
        {
            if (TryParseExactDecimal(decimalText, out var parsed))
            {
                return parsed;
            }

            throw new OverflowException($"TPS DECIMAL field '{name}' cannot be represented by System.Decimal.");
        }

        if (IsNumericType(targetType) && IsNumericType(value.GetType()))
        {
            return ConvertNumeric(value, targetType);
        }

        throw WrongType(name, requestedType.Name, value);
    }

    private static object ConvertArray(
        object?[] elements,
        Type targetType,
        string name,
        TpsFieldType? fieldType)
    {
        if (targetType == typeof(object[])) return ConvertArrayElements<object?>(elements, name, fieldType);
        if (targetType == typeof(string[])) return ConvertArrayElements<string?>(elements, name, fieldType);
        if (targetType == typeof(byte[][])) return ConvertArrayElements<byte[]?>(elements, name, fieldType);

        if (targetType == typeof(byte[])) return ConvertArrayElements<byte>(elements, name, fieldType);
        if (targetType == typeof(sbyte[])) return ConvertArrayElements<sbyte>(elements, name, fieldType);
        if (targetType == typeof(short[])) return ConvertArrayElements<short>(elements, name, fieldType);
        if (targetType == typeof(ushort[])) return ConvertArrayElements<ushort>(elements, name, fieldType);
        if (targetType == typeof(int[])) return ConvertArrayElements<int>(elements, name, fieldType);
        if (targetType == typeof(uint[])) return ConvertArrayElements<uint>(elements, name, fieldType);
        if (targetType == typeof(long[])) return ConvertArrayElements<long>(elements, name, fieldType);
        if (targetType == typeof(ulong[])) return ConvertArrayElements<ulong>(elements, name, fieldType);
        if (targetType == typeof(float[])) return ConvertArrayElements<float>(elements, name, fieldType);
        if (targetType == typeof(double[])) return ConvertArrayElements<double>(elements, name, fieldType);
        if (targetType == typeof(decimal[])) return ConvertArrayElements<decimal>(elements, name, fieldType);

        if (targetType == typeof(byte?[])) return ConvertArrayElements<byte?>(elements, name, fieldType);
        if (targetType == typeof(sbyte?[])) return ConvertArrayElements<sbyte?>(elements, name, fieldType);
        if (targetType == typeof(short?[])) return ConvertArrayElements<short?>(elements, name, fieldType);
        if (targetType == typeof(ushort?[])) return ConvertArrayElements<ushort?>(elements, name, fieldType);
        if (targetType == typeof(int?[])) return ConvertArrayElements<int?>(elements, name, fieldType);
        if (targetType == typeof(uint?[])) return ConvertArrayElements<uint?>(elements, name, fieldType);
        if (targetType == typeof(long?[])) return ConvertArrayElements<long?>(elements, name, fieldType);
        if (targetType == typeof(ulong?[])) return ConvertArrayElements<ulong?>(elements, name, fieldType);
        if (targetType == typeof(float?[])) return ConvertArrayElements<float?>(elements, name, fieldType);
        if (targetType == typeof(double?[])) return ConvertArrayElements<double?>(elements, name, fieldType);
        if (targetType == typeof(decimal?[])) return ConvertArrayElements<decimal?>(elements, name, fieldType);

        if (targetType == typeof(DateOnly[])) return ConvertArrayElements<DateOnly>(elements, name, fieldType);
        if (targetType == typeof(DateOnly?[])) return ConvertArrayElements<DateOnly?>(elements, name, fieldType);
        if (targetType == typeof(TimeOnly[])) return ConvertArrayElements<TimeOnly>(elements, name, fieldType);
        if (targetType == typeof(TimeOnly?[])) return ConvertArrayElements<TimeOnly?>(elements, name, fieldType);

        throw WrongType(name, targetType.Name, elements);
    }

    private static T[] ConvertArrayElements<T>(
        object?[] elements,
        string name,
        TpsFieldType? fieldType)
    {
        var converted = new T[elements.Length];
        for (var index = 0; index < elements.Length; index++)
        {
            converted[index] = (T)ConvertValue(elements[index], typeof(T), name, fieldType)!;
        }

        return converted;
    }

    private static object ConvertNumeric(object value, Type targetType)
    {
        if (targetType == typeof(float) && value is double doubleValue &&
            double.IsFinite(doubleValue) && Math.Abs(doubleValue) > float.MaxValue)
        {
            throw new OverflowException($"Value {doubleValue} is outside the range of System.Single.");
        }

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static bool TryParseExactDecimal(string text, out decimal value)
    {
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (!decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out value) ||
            !TryNormalizeDecimal(text, out var source) ||
            !TryNormalizeDecimal(value.ToString(CultureInfo.InvariantCulture), out var converted) ||
            !string.Equals(source, converted, StringComparison.Ordinal))
        {
            value = default;
            return false;
        }

        return true;
    }

    private static bool TryNormalizeDecimal(string text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var negative = text[0] == '-';
        var start = text[0] is '-' or '+' ? 1 : 0;
        if (start == text.Length)
        {
            return false;
        }

        var decimalPoint = text.IndexOf('.', start);
        if (decimalPoint >= 0 && text.IndexOf('.', decimalPoint + 1) >= 0)
        {
            return false;
        }

        var integer = decimalPoint < 0 ? text[start..] : text[start..decimalPoint];
        var fraction = decimalPoint < 0 ? string.Empty : text[(decimalPoint + 1)..];
        if ((integer.Length == 0 && fraction.Length == 0) ||
            integer.Any(character => !char.IsAsciiDigit(character)) ||
            fraction.Any(character => !char.IsAsciiDigit(character)))
        {
            return false;
        }

        integer = integer.TrimStart('0');
        fraction = fraction.TrimEnd('0');
        integer = integer.Length == 0 ? "0" : integer;
        var isZero = integer == "0" && fraction.Length == 0;
        normalized = (negative && !isZero ? "-" : string.Empty) + integer +
                     (fraction.Length == 0 ? string.Empty : "." + fraction);
        return true;
    }

    private static object? ToPublicValue(object? value)
    {
        if (value is object?[] elements)
        {
            if (elements.All(element => element is TpsGroupValue))
            {
                return elements.Cast<TpsGroupValue>().Select(element => element.Text).ToArray();
            }

            return elements.Select(ToPublicValue).ToArray();
        }

        return value switch
        {
            TpsGroupValue group => group.Text,
            TpsMemoValue memo when memo.Definition.IsMemo => memo.Text,
            TpsMemoValue memo => memo.Blob?.ToArray(),
            byte[] bytes => bytes.ToArray(),
            _ => value
        };
    }

    private static bool IsNumericType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) || type == typeof(sbyte) ||
               type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong) ||
               type == typeof(float) || type == typeof(double) ||
               type == typeof(decimal);
    }

    private static bool IsAccessFailure(Exception exception) =>
        exception is TpsParseException or InvalidCastException or OverflowException or FormatException;

    private static void ValidateElementIndex(int elementIndex)
    {
        if (elementIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex), elementIndex, "The element index cannot be negative.");
        }
    }

    private static TpsParseException WrongType(string name, string expected, object? actual)
    {
        var actualName = actual?.GetType().Name ?? "null";
        return new TpsParseException(new TpsParseError(
            $"Field, MEMO, or BLOB '{name}' cannot be read as {expected}; actual value type is {actualName}."));
    }

    private sealed record ResolvedValue(object? Value, TpsFieldType? FieldType);
}

internal sealed class TpsRecordLayout
{
    private readonly Dictionary<string, int> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _memos = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlySet<string> _ambiguousFields;
    private readonly IReadOnlySet<string> _ambiguousMemos;
    private readonly TpsFieldType[] _fieldTypes;

    public TpsRecordLayout(IReadOnlyList<TpsField> fields, IReadOnlyList<TpsMemo> memos)
    {
        _ambiguousFields = TpsFile.FindAmbiguousAliases(fields, field => field.Name, field => field.ShortName);
        _ambiguousMemos = TpsFile.FindAmbiguousAliases(memos, memo => memo.Name, memo => memo.ShortName);
        _fieldTypes = new TpsFieldType[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            _fieldTypes[i] = field.Type;
            AddNames(_fields, field.Name, field.ShortName, i, _ambiguousFields);
        }

        for (var i = 0; i < memos.Count; i++)
        {
            var memo = memos[i];
            AddNames(_memos, memo.Name, memo.ShortName, i, _ambiguousMemos);
        }

        FieldCount = fields.Count;
        MemoCount = memos.Count;
    }

    public int FieldCount { get; }
    public int MemoCount { get; }

    public bool TryGetField(string name, out int index) => _fields.TryGetValue(name, out index);
    public bool TryGetMemo(string name, out int index) => _memos.TryGetValue(name, out index);
    public bool IsAmbiguous(string name) => _ambiguousFields.Contains(name) || _ambiguousMemos.Contains(name);
    public bool IsMemoAmbiguous(string name) => _ambiguousMemos.Contains(name);
    public TpsFieldType GetFieldType(int index) => _fieldTypes[index];

    private static void AddNames(
        IDictionary<string, int> names,
        string fullName,
        string shortName,
        int index,
        IReadOnlySet<string> ambiguousAliases)
    {
        names.Add(fullName, index);
        if (!string.IsNullOrWhiteSpace(shortName) && !ambiguousAliases.Contains(shortName))
        {
            names.TryAdd(shortName, index);
        }
    }
}

internal sealed class TpsMemoValue
{
    public TpsMemoValue(
        TpsMemo definition,
        string? text,
        byte[]? blob,
        TpsMemoState? state = null)
    {
        Definition = definition;
        Text = text;
        Blob = blob;
        State = state ?? (text is null && blob is null ? TpsMemoState.Empty : TpsMemoState.Complete);
    }

    public TpsMemo Definition { get; }
    public string? Text { get; }
    public byte[]? Blob { get; }
    public TpsMemoState State { get; }
}
