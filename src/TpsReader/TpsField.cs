namespace TpsReader;

/// <summary>Describes a field declared by a TPS table.</summary>
public sealed class TpsField
{
    internal TpsField(
        int fieldNumber,
        string name,
        string shortName,
        string tablePrefix,
        TpsFieldType type,
        int offset,
        int length,
        int elementCount,
        int decimalDigits,
        int decimalStorageLength,
        int rawTypeCode = 0,
        int flags = 0,
        int indexNumber = 0,
        int stringLength = 0,
        string? stringMask = null)
    {
        FieldNumber = fieldNumber;
        Name = name;
        ShortName = shortName;
        TablePrefix = tablePrefix;
        Type = type;
        Offset = offset;
        Length = length;
        ElementCount = elementCount;
        DecimalDigits = decimalDigits;
        DecimalStorageLength = decimalStorageLength;
        RawTypeCode = rawTypeCode;
        Flags = flags;
        IndexNumber = indexNumber;
        StringLength = stringLength;
        StringMask = stringMask ?? string.Empty;
    }

    /// <summary>Gets the one-based field ordinal in the table schema.</summary>
    public int FieldNumber { get; }

    /// <summary>Gets the full schema name.</summary>
    public string Name { get; }

    /// <summary>Gets the name without its table prefix.</summary>
    public string ShortName { get; }

    /// <summary>Gets the table prefix embedded in the field name.</summary>
    public string TablePrefix { get; }

    /// <summary>Gets the decoded field type.</summary>
    public TpsFieldType Type { get; }

    /// <summary>Gets the field's byte offset in a record.</summary>
    public int Offset { get; }

    /// <summary>Gets the field's total declared width in bytes.</summary>
    public int Length { get; }

    /// <summary>Gets the number of declared array elements.</summary>
    public int ElementCount { get; }

    /// <summary>Gets the number of fractional digits declared for a DECIMAL field.</summary>
    public int DecimalDigits { get; }

    /// <summary>Gets the declared binary storage width for a DECIMAL field.</summary>
    public int DecimalStorageLength { get; }

    /// <summary>Gets the raw TPS field type code.</summary>
    public int RawTypeCode { get; }

    /// <summary>Gets the raw TPS field flags.</summary>
    public int Flags { get; }

    /// <summary>Gets the raw TPS index number associated with the field.</summary>
    public int IndexNumber { get; }

    /// <summary>Gets the string length stored in TPS metadata.</summary>
    public int StringLength { get; }

    /// <summary>Gets the string mask stored in TPS metadata.</summary>
    public string StringMask { get; }

    /// <summary>Gets whether the field contains multiple elements.</summary>
    public bool IsArray => ElementCount > 1;

    /// <summary>Gets whether the field is a GROUP.</summary>
    public bool IsGroup => Type == TpsFieldType.Group;
}
