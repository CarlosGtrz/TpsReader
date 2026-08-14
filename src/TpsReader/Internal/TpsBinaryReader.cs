using System.Globalization;
using System.Text;

namespace TpsReader.Internal;

internal sealed class TpsBinaryReader
{
    private readonly byte[] _data;
    private readonly int _baseOffset;
    private readonly int _length;
    private readonly Stack<int> _savedPositions = [];
    private int _position;

    public TpsBinaryReader(byte[] data)
        : this(data, 0, data?.Length ?? throw new ArgumentNullException(nameof(data)))
    {
    }

    private TpsBinaryReader(byte[] data, int baseOffset, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (baseOffset < 0 || length < 0 || baseOffset > data.Length - length)
        {
            throw new InvalidDataException($"Invalid byte range: offset={baseOffset}, length={length}, buffer={data.Length}.");
        }

        _data = data;
        _baseOffset = baseOffset;
        _length = length;
    }

    public int Position => _position;
    public int Length => _length;
    public int Remaining => _length - _position;

    public void SavePosition() => _savedPositions.Push(_position);

    public void RestorePosition()
    {
        if (_savedPositions.Count == 0)
        {
            throw new InvalidOperationException("No saved reader position is available.");
        }

        _position = _savedPositions.Pop();
    }

    public int ReadInt32LittleEndian()
    {
        EnsureAvailable(4);
        var offset = _baseOffset + _position;
        var value = unchecked(
            _data[offset] |
            (_data[offset + 1] << 8) |
            (_data[offset + 2] << 16) |
            (_data[offset + 3] << 24));
        _position += 4;
        return value;
    }

    public long ReadUInt32LittleEndian()
    {
        EnsureAvailable(4);
        var offset = _baseOffset + _position;
        var value =
            _data[offset] |
            ((long)_data[offset + 1] << 8) |
            ((long)_data[offset + 2] << 16) |
            ((long)_data[offset + 3] << 24);
        _position += 4;
        return value;
    }

    public int ReadInt32BigEndian()
    {
        EnsureAvailable(4);
        var offset = _baseOffset + _position;
        var value = unchecked(
            _data[offset + 3] |
            (_data[offset + 2] << 8) |
            (_data[offset + 1] << 16) |
            (_data[offset] << 24));
        _position += 4;
        return value;
    }

    public short ReadInt16LittleEndian()
    {
        EnsureAvailable(2);
        var offset = _baseOffset + _position;
        var value = unchecked((short)(_data[offset] | (_data[offset + 1] << 8)));
        _position += 2;
        return value;
    }

    public ushort ReadUInt16LittleEndian()
    {
        EnsureAvailable(2);
        var offset = _baseOffset + _position;
        var value = (ushort)(_data[offset] | (_data[offset + 1] << 8));
        _position += 2;
        return value;
    }

    public ushort ReadUInt16BigEndian()
    {
        EnsureAvailable(2);
        var offset = _baseOffset + _position;
        var value = (ushort)(_data[offset + 1] | (_data[offset] << 8));
        _position += 2;
        return value;
    }

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _data[_baseOffset + _position++];
    }

    public byte PeekByte(int position)
    {
        if ((uint)position >= (uint)_length)
        {
            throw InvalidRange(position, 1);
        }

        return _data[_baseOffset + position];
    }

    public float ReadSingleLittleEndian() => BitConverter.Int32BitsToSingle(ReadInt32LittleEndian());

    public double ReadDoubleLittleEndian()
    {
        var low = ReadInt32LittleEndian() & 0xFFFFFFFFL;
        var high = ReadInt32LittleEndian() & 0xFFFFFFFFL;
        return BitConverter.Int64BitsToDouble((high << 32) | low);
    }

    public string ReadFixedString(int length, Encoding? encoding = null)
    {
        EnsureAvailable(length);
        var result = (encoding ?? Encoding.Latin1).GetString(_data, _baseOffset + _position, length);
        _position += length;
        return result;
    }

    public string ReadNullTerminatedString(Encoding? encoding = null)
    {
        var start = _position;
        while (ReadByte() != 0)
        {
        }

        var byteCount = _position - start - 1;
        return (encoding ?? Encoding.Latin1).GetString(_data, _baseOffset + start, byteCount);
    }

    public string ReadPascalString(Encoding encoding)
    {
        var length = ReadByte();
        return encoding.GetString(ReadBytes(length));
    }

    public TpsBinaryReader Seek(int position)
    {
        if (position < 0 || position > _length)
        {
            throw InvalidRange(position, 0);
        }

        _position = position;
        return this;
    }

    public void Advance(int count) => Seek(checked(_position + count));

    public TpsBinaryReader ReadSlice(int length)
    {
        EnsureAvailable(length);
        var result = new TpsBinaryReader(_data, _baseOffset + _position, length);
        _position += length;
        return result;
    }

    public byte[] ReadBytes(int length)
    {
        EnsureAvailable(length);
        var result = new byte[length];
        Array.Copy(_data, _baseOffset + _position, result, 0, length);
        _position += length;
        return result;
    }

    public byte[] RemainingBytes()
    {
        var result = new byte[Remaining];
        Array.Copy(_data, _baseOffset + _position, result, 0, result.Length);
        return result;
    }

    public byte[] ToArray()
    {
        var result = new byte[_length];
        Array.Copy(_data, _baseOffset, result, 0, result.Length);
        return result;
    }

    public void CopyPrefixTo(byte[] destination, int count)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (count < 0 || count > _length || count > destination.Length)
        {
            throw new InvalidDataException($"Cannot copy {count} bytes from a {_length}-byte buffer into a {destination.Length}-byte destination.");
        }

        Array.Copy(_data, _baseOffset, destination, 0, count);
    }

    public int[] ReadInt32LittleEndianArray(int count)
    {
        if (count < 0)
        {
            throw new InvalidDataException($"Invalid array element count {count}.");
        }

        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = ReadInt32LittleEndian();
        }

        return result;
    }

    public string ReadBinaryCodedDecimal(int length, int decimalDigits)
    {
        var text = Convert.ToHexString(ReadBytes(length));
        if (text.Length == 0)
        {
            throw new InvalidDataException("A BCD value cannot be empty.");
        }

        var negative = text[0] != '0';
        var number = text[1..];
        if (decimalDigits < 0 || decimalDigits > number.Length)
        {
            throw new InvalidDataException($"BCD decimal digit count {decimalDigits} exceeds the available {number.Length} digits.");
        }

        if (decimalDigits > 0)
        {
            var decimalIndex = number.Length - decimalDigits;
            number = TrimLeadingZeros(number[..decimalIndex]) + "." + number[decimalIndex..];
        }
        else
        {
            number = TrimLeadingZeros(number);
        }

        return (negative ? "-" : string.Empty) + number;
    }

    public void WriteInt32LittleEndian(int value)
    {
        EnsureAvailable(4);
        var offset = _baseOffset + _position;
        _data[offset] = (byte)value;
        _data[offset + 1] = (byte)(value >> 8);
        _data[offset + 2] = (byte)(value >> 16);
        _data[offset + 3] = (byte)(value >> 24);
        _position += 4;
    }

    private void EnsureAvailable(int count)
    {
        if (count < 0 || _position < 0 || count > _length - _position)
        {
            throw InvalidRange(_position, count);
        }
    }

    private InvalidDataException InvalidRange(int position, int count) =>
        new($"Cannot read {count} byte(s) at position {position}; buffer length is {_length}.");

    private static string TrimLeadingZeros(string number)
    {
        var trimmed = number.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }
}

internal static class TpsRunLengthEncoding
{
    public static TpsBinaryReader Decompress(TpsBinaryReader compressed, int expectedLength)
    {
        if (expectedLength < 0)
        {
            throw new InvalidDataException($"Invalid expected RLE output length {expectedLength}.");
        }

        var output = new byte[expectedLength];
        var outputPosition = 0;
        if (compressed.Remaining == 0)
        {
            if (expectedLength == 0)
            {
                return new TpsBinaryReader([]);
            }

            throw new InvalidDataException($"RLE output is empty; expected {expectedLength} bytes.");
        }

        do
        {
            var literalCount = ReadCount(compressed, "literal");
            EnsureOutputCapacity(outputPosition, literalCount, expectedLength);
            var literals = compressed.ReadBytes(literalCount);
            literals.CopyTo(output, outputPosition);
            outputPosition += literalCount;

            if (compressed.Remaining == 0)
            {
                break;
            }

            compressed.Advance(-1);
            var repeatedByte = compressed.ReadByte();
            var repeatCount = ReadExtendedCount(compressed.ReadByte(), compressed);
            EnsureOutputCapacity(outputPosition, repeatCount, expectedLength);
            output.AsSpan(outputPosition, repeatCount).Fill(repeatedByte);
            outputPosition += repeatCount;
        }
        while (compressed.Remaining > 1);

        if (outputPosition != expectedLength)
        {
            throw new InvalidDataException($"RLE output contains {outputPosition} bytes; expected {expectedLength}.");
        }

        return new TpsBinaryReader(output);
    }

    private static void EnsureOutputCapacity(long currentLength, int additionalLength, int expectedLength)
    {
        if (additionalLength < 0 || currentLength + additionalLength > expectedLength)
        {
            throw new InvalidDataException($"RLE output exceeds its declared length of {expectedLength} bytes.");
        }
    }

    private static int ReadCount(TpsBinaryReader reader, string kind)
    {
        var value = reader.ReadByte();
        if (value == 0)
        {
            throw new InvalidDataException($"Invalid RLE {kind} count 0x00 at position {reader.Position - 1}.");
        }

        return ReadExtendedCount(value, reader);
    }

    private static int ReadExtendedCount(int value, TpsBinaryReader reader)
    {
        if (value <= 0x7F)
        {
            return value;
        }

        var mostSignificant = reader.ReadByte();
        var leastSignificant = value & 0x7F;
        var shift = 0x80 * (mostSignificant & 0x01);
        return ((mostSignificant << 7) & 0x00FF00) + leastSignificant + shift;
    }
}
