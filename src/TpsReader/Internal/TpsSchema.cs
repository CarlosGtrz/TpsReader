using System.Text;

namespace TpsReader.Internal;

internal sealed class FieldDefinitionRecord
{
    public FieldDefinitionRecord(TpsBinaryReader reader, Encoding textEncoding)
    {
        FieldType = reader.ReadByte();
        Offset = reader.ReadUInt16LittleEndian();
        Name = reader.ReadNullTerminatedString(textEncoding);
        ElementCount = reader.ReadUInt16LittleEndian();
        Length = reader.ReadUInt16LittleEndian();
        Flags = reader.ReadUInt16LittleEndian();
        IndexNumber = reader.ReadUInt16LittleEndian();

        switch (FieldType)
        {
            case 0x0A:
                DecimalDigits = reader.ReadByte();
                DecimalStorageLength = reader.ReadByte();
                break;
            case 0x12:
            case 0x13:
            case 0x14:
                StringLength = reader.ReadUInt16LittleEndian();
                StringMask = reader.ReadNullTerminatedString(textEncoding);
                if (StringMask.Length == 0)
                {
                    _ = reader.ReadByte();
                }

                break;
        }
    }

    public int FieldType { get; }
    public int Offset { get; }
    public string Name { get; }
    public string ShortName => StripPrefix(Name);
    public string TablePrefix
    {
        get
        {
            var separator = Name.IndexOf(':', StringComparison.Ordinal);
            return separator > 0 ? Name[..separator] : string.Empty;
        }
    }

    public int ElementCount { get; }
    public int Length { get; }
    public int DecimalDigits { get; }
    public int DecimalStorageLength { get; }
    public int Flags { get; }
    public int IndexNumber { get; }
    public int StringLength { get; }
    public string StringMask { get; } = string.Empty;
    public bool IsArray => ElementCount > 1;

    private static string StripPrefix(string name)
    {
        var separator = name.IndexOf(':', StringComparison.Ordinal);
        return separator >= 0 ? name[(separator + 1)..] : name;
    }
}

internal sealed class TableDefinitionRecord
{
    private readonly Encoding _textEncoding;

    public TableDefinitionRecord(TpsBinaryReader reader, Encoding textEncoding)
    {
        DriverVersion = reader.ReadUInt16LittleEndian();
        RecordLength = reader.ReadUInt16LittleEndian();
        var fieldCount = reader.ReadUInt16LittleEndian();
        var memoCount = reader.ReadUInt16LittleEndian();
        var indexCount = reader.ReadUInt16LittleEndian();
        _textEncoding = textEncoding;

        try
        {
            for (var i = 0; i < fieldCount; i++)
            {
                Fields.Add(new FieldDefinitionRecord(reader, textEncoding));
            }

            for (var i = 0; i < memoCount; i++)
            {
                Memos.Add(new MemoDefinitionRecord(reader, textEncoding));
            }

            for (var i = 0; i < indexCount; i++)
            {
                Indexes.Add(new IndexDefinitionRecord(reader, textEncoding));
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("Invalid TPS table definition.", ex);
        }
    }

    public List<FieldDefinitionRecord> Fields { get; } = [];
    public List<MemoDefinitionRecord> Memos { get; } = [];
    public List<IndexDefinitionRecord> Indexes { get; } = [];
    public int DriverVersion { get; }
    public int RecordLength { get; }

    public object?[] ParseRecord(byte[] record)
    {
        var reader = new TpsBinaryReader(record);
        var values = new object?[Fields.Count];
        for (var fieldIndex = 0; fieldIndex < Fields.Count; fieldIndex++)
        {
            var field = Fields[fieldIndex];
            if (field.FieldType == 0x16)
            {
                values[fieldIndex] = ParseGroup(field, record);
                continue;
            }

            if (field.IsArray)
            {
                if (field.ElementCount <= 0 || field.Length % field.ElementCount != 0)
                {
                    throw new InvalidDataException($"Field '{field.Name}' has invalid array dimensions.");
                }

                var elements = new object?[field.ElementCount];
                var elementLength = field.Length / elements.Length;
                for (var elementIndex = 0; elementIndex < elements.Length; elementIndex++)
                {
                    elements[elementIndex] = ParseField(field, field.Offset + elementLength * elementIndex, elementLength, reader);
                }

                values[fieldIndex] = elements;
            }
            else
            {
                values[fieldIndex] = ParseField(field, field.Offset, field.Length, reader);
            }
        }

        return values;
    }

    private object? ParseField(FieldDefinitionRecord field, int offset, int length, TpsBinaryReader reader)
    {
        reader.Seek(offset);
        return field.FieldType switch
        {
            1 => ReadByte(reader, length),
            2 => ReadInt16(reader, length),
            3 => ReadUInt16(reader, length),
            4 => ReadDate(reader, length),
            5 => ReadTime(reader, length),
            6 => ReadInt32(reader, length),
            7 => ReadUInt32(reader, length),
            8 => ReadSingle(reader, length),
            9 => ReadDouble(reader, length),
            0x0A => reader.ReadBinaryCodedDecimal(length, field.DecimalDigits),
            0x12 => reader.ReadFixedString(length, _textEncoding).TrimEnd('\0', ' '),
            0x13 => ReadCString(reader, length),
            0x14 => ReadPascalString(reader, length),
            0x16 => throw new InvalidDataException("GROUP fields must be parsed through their projected layout."),
            _ => throw new InvalidDataException($"Unsupported TPS field type {field.FieldType} ({length} bytes).")
        };
    }

    private object ParseGroup(FieldDefinitionRecord group, byte[] record)
    {
        if (group.ElementCount <= 0 || group.Length <= 0 || group.Length % group.ElementCount != 0)
        {
            throw new InvalidDataException($"GROUP field '{group.Name}' has invalid array dimensions.");
        }

        ValidateRange(group.Offset, group.Length, record.Length, group.Name);
        var elementWidth = group.Length / group.ElementCount;
        var prototypeEnd = checked(group.Offset + elementWidth);
        var components = new List<GroupStringComponent>();

        foreach (var field in Fields.Where(field => field.FieldType is 0x12 or 0x13 or 0x14))
        {
            if (field.Offset < group.Offset || field.Offset >= prototypeEnd)
            {
                continue;
            }

            if (field.ElementCount <= 0 || field.Length <= 0 || field.Length % field.ElementCount != 0)
            {
                throw new InvalidDataException($"String field '{field.Name}' has invalid array dimensions inside GROUP '{group.Name}'.");
            }

            var fieldEnd = checked(field.Offset + field.Length);
            if (fieldEnd > prototypeEnd)
            {
                throw new InvalidDataException(
                    $"String field '{field.Name}' extends outside the prototype element of GROUP '{group.Name}'.");
            }

            var componentWidth = field.Length / field.ElementCount;
            for (var elementIndex = 0; elementIndex < field.ElementCount; elementIndex++)
            {
                var relativeOffset = checked(field.Offset - group.Offset + elementIndex * componentWidth);
                ValidateRange(relativeOffset, componentWidth, elementWidth, field.Name);
                components.Add(new GroupStringComponent(field, relativeOffset, componentWidth));
            }
        }

        var elements = new object?[group.ElementCount];
        for (var elementIndex = 0; elementIndex < group.ElementCount; elementIndex++)
        {
            var sourceOffset = checked(group.Offset + elementIndex * elementWidth);
            ValidateRange(sourceOffset, elementWidth, record.Length, group.Name);
            var rawBytes = record.AsSpan(sourceOffset, elementWidth).ToArray();
            var projection = new byte[elementWidth];
            Array.Fill(projection, (byte)0x20);

            foreach (var component in components)
            {
                var componentOffset = checked(sourceOffset + component.RelativeOffset);
                ValidateRange(componentOffset, component.Width, record.Length, component.Field.Name);
                var text = DecodeGroupString(component.Field.FieldType, record, componentOffset, component.Width);
                var encoded = _textEncoding.GetBytes(text);
                if (encoded.Length > component.Width)
                {
                    throw new InvalidDataException(
                        $"String field '{component.Field.Name}' does not fit its {component.Width}-byte GROUP allocation.");
                }

                encoded.CopyTo(projection, component.RelativeOffset);
            }

            elements[elementIndex] = new TpsGroupValue(_textEncoding.GetString(projection), rawBytes);
        }

        return group.IsArray ? elements : elements[0]!;
    }

    private string DecodeGroupString(int fieldType, byte[] record, int offset, int length)
    {
        var bytes = record.AsSpan(offset, length);
        return fieldType switch
        {
            0x12 => _textEncoding.GetString(bytes).TrimEnd('\0', ' '),
            0x13 => DecodeGroupCString(bytes),
            0x14 => DecodeGroupPascalString(bytes),
            _ => throw new InvalidDataException($"Field type {fieldType} cannot be projected into GROUP text.")
        };
    }

    private string DecodeGroupCString(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        var content = terminator < 0 ? bytes : bytes[..terminator];
        return _textEncoding.GetString(content);
    }

    private string DecodeGroupPascalString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new InvalidDataException("A TPS PSTRING GROUP component cannot have zero width.");
        }

        var contentLength = bytes[0];
        if (contentLength > bytes.Length - 1)
        {
            throw new InvalidDataException(
                $"A TPS PSTRING GROUP component declares {contentLength} bytes but only {bytes.Length - 1} are available.");
        }

        return _textEncoding.GetString(bytes.Slice(1, contentLength));
    }

    private static void ValidateRange(int offset, int length, int bufferLength, string fieldName)
    {
        if (offset < 0 || length < 0 || offset > bufferLength - length)
        {
            throw new InvalidDataException(
                $"Field '{fieldName}' has invalid byte range offset={offset}, length={length}, buffer={bufferLength}.");
        }
    }

    private sealed record GroupStringComponent(FieldDefinitionRecord Field, int RelativeOffset, int Width);

    private string ReadCString(TpsBinaryReader reader, int length)
    {
        var field = reader.ReadSlice(length);
        return field.ReadNullTerminatedString(_textEncoding);
    }

    private string ReadPascalString(TpsBinaryReader reader, int length)
    {
        var field = reader.ReadSlice(length);
        return field.ReadPascalString(_textEncoding);
    }

    private static byte ReadByte(TpsBinaryReader reader, int length)
    {
        RequireLength(1, length);
        return reader.ReadByte();
    }

    private static short ReadInt16(TpsBinaryReader reader, int length)
    {
        RequireLength(2, length);
        return reader.ReadInt16LittleEndian();
    }

    private static ushort ReadUInt16(TpsBinaryReader reader, int length)
    {
        RequireLength(2, length);
        return reader.ReadUInt16LittleEndian();
    }

    private static int ReadInt32(TpsBinaryReader reader, int length)
    {
        RequireLength(4, length);
        return reader.ReadInt32LittleEndian();
    }

    private static long ReadUInt32(TpsBinaryReader reader, int length)
    {
        RequireLength(4, length);
        return reader.ReadUInt32LittleEndian();
    }

    private static float ReadSingle(TpsBinaryReader reader, int length)
    {
        RequireLength(4, length);
        return reader.ReadSingleLittleEndian();
    }

    private static double ReadDouble(TpsBinaryReader reader, int length)
    {
        RequireLength(8, length);
        return reader.ReadDoubleLittleEndian();
    }

    private static DateOnly? ReadDate(TpsBinaryReader reader, int length)
    {
        RequireLength(4, length);
        var value = reader.ReadUInt32LittleEndian();
        if (value == 0)
        {
            return null;
        }

        var year = (int)((value & 0xFFFF0000) >> 16);
        var month = (int)((value & 0x0000FF00) >> 8);
        var day = (int)(value & 0x000000FF);
        try
        {
            return new DateOnly(year, month, day);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException($"Invalid TPS date {year:D4}-{month:D2}-{day:D2}.", ex);
        }
    }

    private static TimeOnly ReadTime(TpsBinaryReader reader, int length)
    {
        RequireLength(4, length);
        var value = reader.ReadUInt32LittleEndian();
        var hundredths = (int)(value & 0xFF);
        var second = (int)((value >> 8) & 0xFF);
        var minute = (int)((value >> 16) & 0xFF);
        var hour = (int)((value >> 24) & 0xFF);
        try
        {
            return new TimeOnly(hour, minute, second, hundredths * 10);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException($"Invalid TPS time {hour:D2}:{minute:D2}:{second:D2}.{hundredths:D2}.", ex);
        }
    }

    private static void RequireLength(int expected, int actual)
    {
        if (expected != actual)
        {
            throw new InvalidDataException($"Expected a {expected}-byte TPS field, found {actual} bytes.");
        }
    }
}

internal sealed class DataRecord
{
    public DataRecord(RawTpsRecord record, TableDefinitionRecord tableDefinition)
    {
        var header = record.Header as DataHeader
            ?? throw new InvalidDataException("TPS data record has an invalid header.");
        RecordNumber = header.RecordNumber;
        SourcePageOffset = record.SourcePageOffset;
        Values = tableDefinition.ParseRecord(record.Data.RemainingBytes());
    }

    public int RecordNumber { get; }
    public int SourcePageOffset { get; }
    public object?[] Values { get; }
}

internal sealed class IndexDefinitionRecord
{
    public IndexDefinitionRecord(TpsBinaryReader reader, Encoding textEncoding)
    {
        ExternalName = reader.ReadNullTerminatedString(textEncoding);
        if (ExternalName.Length == 0 && reader.ReadByte() != 1)
        {
            throw new InvalidDataException("Invalid TPS index definition terminator.");
        }

        Name = reader.ReadNullTerminatedString(textEncoding);
        Flags = reader.ReadByte();
        FieldCount = reader.ReadUInt16LittleEndian();
        for (var i = 0; i < FieldCount; i++)
        {
            Components.Add(new IndexComponentDefinitionRecord(
                i + 1,
                reader.ReadUInt16LittleEndian(),
                reader.ReadUInt16LittleEndian()));
        }
    }

    public string Name { get; }
    public string ExternalName { get; }
    public int Flags { get; }
    public int FieldCount { get; }
    public List<IndexComponentDefinitionRecord> Components { get; } = [];
}

internal sealed record IndexComponentDefinitionRecord(int Rank, int FieldIndex, int Flags);

internal sealed class MemoDefinitionRecord
{
    public MemoDefinitionRecord(TpsBinaryReader reader, Encoding textEncoding)
    {
        ExternalName = reader.ReadNullTerminatedString(textEncoding);
        if (ExternalName.Length == 0 && reader.ReadByte() != 1)
        {
            throw new InvalidDataException("Invalid TPS MEMO definition terminator.");
        }

        Name = reader.ReadNullTerminatedString(textEncoding);
        Length = reader.ReadUInt16LittleEndian();
        Flags = reader.ReadUInt16LittleEndian();
    }

    public string Name { get; }
    public string ShortName
    {
        get
        {
            var separator = Name.IndexOf(':', StringComparison.Ordinal);
            return separator >= 0 ? Name[(separator + 1)..] : Name;
        }
    }

    public int Flags { get; }
    public int Length { get; }
    public string ExternalName { get; }
    public bool IsBlob => (Flags & TpsFormatConstants.BlobFlag) != 0;
}

internal sealed class MemoRecord
{
    private readonly byte[] _data;

    public MemoRecord(MemoHeader header, TpsBinaryReader data, TpsMemoState fragmentState = TpsMemoState.Complete)
    {
        OwnerRecordNumber = header.OwnerRecordNumber;
        _data = data.ToArray();
        FragmentState = fragmentState;
    }

    public int OwnerRecordNumber { get; }
    public TpsMemoState FragmentState { get; }

    public string ReadText(Encoding textEncoding) => textEncoding.GetString(_data);

    public byte[] ReadBlob(bool ignoreErrors) => ReadBlob(ignoreErrors, out _);

    public byte[] ReadBlob(bool ignoreErrors, out TpsMemoState state)
    {
        state = FragmentState;
        if (FragmentState == TpsMemoState.Damaged && !ignoreErrors)
        {
            throw new InvalidDataException("TPS BLOB fragments are incomplete.");
        }

        var reader = new TpsBinaryReader(_data);
        if (reader.Remaining < 4)
        {
            state = TpsMemoState.Damaged;
            if (ignoreErrors)
            {
                return [];
            }

            throw new InvalidDataException("TPS BLOB is missing its four-byte length header.");
        }

        var declaredLength = reader.ReadInt32LittleEndian();
        if (declaredLength < 0)
        {
            state = TpsMemoState.Damaged;
            if (ignoreErrors)
            {
                return reader.RemainingBytes();
            }

            throw new InvalidDataException($"TPS BLOB declares negative length {declaredLength}.");
        }

        if (declaredLength > reader.Remaining)
        {
            state = TpsMemoState.Damaged;
            if (ignoreErrors)
            {
                return reader.RemainingBytes();
            }

            throw new InvalidDataException($"TPS BLOB declares {declaredLength} bytes but only {reader.Remaining} are available.");
        }

        return reader.ReadBytes(declaredLength);
    }
}

internal sealed class TableNameRecord
{
    public TableNameRecord(RawTpsRecord record)
    {
        var header = record.Header as TableNameHeader
            ?? throw new InvalidDataException("TPS table-name record has an invalid header.");
        Name = header.Name;
        TableNumber = record.Data.ReadInt32BigEndian();
    }

    public int TableNumber { get; }
    public string Name { get; }
}
