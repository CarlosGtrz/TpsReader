using System.Text;

namespace TpsReader.Internal;

internal sealed class TpsHeader
{
    public TpsHeader(TpsBinaryReader reader)
    {
        if (reader.ReadInt32LittleEndian() != 0)
        {
            throw new InvalidDataException("File does not start with 0x00000000; it is not a TopSpeed file or it may be encrypted.");
        }

        var headerSize = reader.ReadUInt16LittleEndian();
        if (headerSize < 0x200)
        {
            throw new InvalidDataException($"TopSpeed header is too small ({headerSize} bytes).");
        }

        var header = reader.ReadSlice(headerSize - 6);
        _ = header.ReadInt32LittleEndian();
        _ = header.ReadInt32LittleEndian();
        var signature = header.ReadFixedString(4);
        if (signature != "tOpS")
        {
            throw new InvalidDataException($"Invalid TopSpeed signature '{signature}'.");
        }

        _ = header.ReadUInt16LittleEndian();
        _ = header.ReadInt32BigEndian();
        _ = header.ReadInt32LittleEndian();
        _ = ToFileOffset(header.ReadInt32LittleEndian());
        PageStarts = header.ReadInt32LittleEndianArray((0x110 - 0x20) / 4).Select(ToFileOffset).ToArray();
        PageEnds = header.ReadInt32LittleEndianArray((0x200 - 0x110) / 4).Select(ToFileOffset).ToArray();
    }

    public IReadOnlyList<int> PageStarts { get; }
    public IReadOnlyList<int> PageEnds { get; }

    private static int ToFileOffset(int pageReference)
    {
        var offset = ((long)pageReference << 8) + 0x200;
        if (offset < 0 || offset > int.MaxValue)
        {
            throw new InvalidDataException($"Invalid TPS page reference {pageReference}.");
        }

        return (int)offset;
    }
}

internal sealed class TpsBlock
{
    private readonly List<TpsPage> _pages = [];
    private readonly TpsBinaryReader _reader;
    private readonly int _end;

    public TpsBlock(
        TpsBinaryReader reader,
        int start,
        int end,
        bool ignoreErrors,
        Action? reportRecoveryIssue = null)
    {
        if (start < 0 || end < start || end > reader.Length)
        {
            throw new InvalidDataException($"Invalid TPS block range: start={start}, end={end}, file={reader.Length}.");
        }

        _reader = reader;
        _end = end;
        reader.SavePosition();
        reader.Seek(start);
        try
        {
            while (reader.Position < end)
            {
                try
                {
                    if (IsCompletePage())
                    {
                        _pages.Add(new TpsPage(reader));
                    }
                    else
                    {
                        AdvanceOneSector();
                    }
                }
                catch (InvalidDataException) when (ignoreErrors)
                {
                    reportRecoveryIssue?.Invoke();
                    AdvanceOneSector();
                }

                NavigateToNextPage();
            }
        }
        finally
        {
            reader.RestorePosition();
        }
    }

    public IReadOnlyList<TpsPage> Pages => _pages;

    private void AdvanceOneSector()
    {
        var next = Math.Min(_end, (_reader.Position & ~0xFF) + 0x100);
        if (next <= _reader.Position)
        {
            next = Math.Min(_end, _reader.Position + 0x100);
        }

        _reader.Seek(next);
    }

    private void NavigateToNextPage()
    {
        if ((_reader.Position & 0xFF) != 0)
        {
            _reader.Seek(Math.Min(_end, (_reader.Position & ~0xFF) + 0x100));
        }

        while (_reader.Position < _end && _end - _reader.Position >= 4)
        {
            _reader.SavePosition();
            var address = _reader.ReadInt32LittleEndian();
            _reader.RestorePosition();
            if (address == _reader.Position)
            {
                break;
            }

            _reader.Seek(Math.Min(_end, _reader.Position + 0x100));
        }
    }

    private bool IsCompletePage()
    {
        _reader.SavePosition();
        try
        {
            var pageStart = _reader.Position;
            _ = _reader.ReadInt32LittleEndian();
            var pageSize = _reader.ReadUInt16LittleEndian();
            if (pageSize < 13 || pageStart + pageSize > _end)
            {
                throw new InvalidDataException($"Invalid TPS page size {pageSize} at offset {pageStart}.");
            }

            for (var offset = 0x100; offset < pageSize; offset += 0x100)
            {
                if (offset > _end - pageStart - 4)
                {
                    return false;
                }

                var sectorPosition = pageStart + offset;
                _reader.Seek(sectorPosition);
                if (_reader.ReadInt32LittleEndian() == sectorPosition)
                {
                    if (sectorPosition <= _end - 6 && sectorPosition <= _reader.Length - 6)
                    {
                        var nestedPageSize = _reader.ReadUInt16LittleEndian();
                        if (nestedPageSize >= 13 &&
                            nestedPageSize <= _end - sectorPosition &&
                            nestedPageSize <= _reader.Length - sectorPosition)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
        finally
        {
            _reader.RestorePosition();
        }
    }
}

internal sealed class TpsPage
{
    private readonly int _pageSize;
    private readonly int _uncompressedPageSize;
    private readonly int _recordCount;
    private readonly byte _flags;
    private readonly byte[] _storedData;

    public TpsPage(TpsBinaryReader reader)
    {
        SourceOffset = reader.Position;
        _ = reader.ReadInt32LittleEndian();
        _pageSize = reader.ReadUInt16LittleEndian();
        if (_pageSize < 13)
        {
            throw new InvalidDataException($"Invalid TPS page size {_pageSize}.");
        }

        var page = reader.ReadSlice(_pageSize - 6);
        _uncompressedPageSize = page.ReadUInt16LittleEndian();
        _ = page.ReadUInt16LittleEndian();
        _recordCount = page.ReadUInt16LittleEndian();
        _flags = page.ReadByte();
        _storedData = page.ReadBytes(_pageSize - 13);
    }

    public int SourceOffset { get; }
    public int EndOffset => checked(SourceOffset + _pageSize);

    public IReadOnlyList<RawTpsRecord> ReadRecords(Encoding textEncoding)
    {
        if (_flags != 0)
        {
            return [];
        }

        var source = new TpsBinaryReader(_storedData);
        var data = _pageSize != _uncompressedPageSize
            ? TpsRunLengthEncoding.Decompress(source, _uncompressedPageSize - 13)
            : source;

        var records = new List<RawTpsRecord>(_recordCount);
        RawTpsRecord? previous = null;
        while (data.Remaining > 1 && records.Count < _recordCount)
        {
            var current = previous is null
                ? RawTpsRecord.ReadFirst(data, textEncoding, SourceOffset)
                : RawTpsRecord.ReadNext(previous, data, textEncoding);
            records.Add(current);
            previous = current;
        }

        if (records.Count != _recordCount)
        {
            throw new InvalidDataException($"TPS page declares {_recordCount} records but {records.Count} were decoded.");
        }

        return records;
    }
}

internal sealed class RawTpsRecord
{
    private RawTpsRecord(
        int recordLength,
        int headerLength,
        byte[] data,
        Encoding textEncoding,
        int sourcePageOffset)
    {
        RecordLength = recordLength;
        HeaderLength = headerLength;
        SourcePageOffset = sourcePageOffset;
        Data = new TpsBinaryReader(data);
        BuildHeader(textEncoding);
    }

    public int RecordLength { get; }
    public int HeaderLength { get; }
    public int SourcePageOffset { get; }
    public TpsBinaryReader Data { get; }
    public RecordHeader? Header { get; private set; }

    public static RawTpsRecord ReadFirst(TpsBinaryReader reader, Encoding textEncoding, int sourcePageOffset = 0)
    {
        var flags = reader.ReadByte();
        if ((flags & 0xC0) != 0xC0)
        {
            throw new InvalidDataException($"First TPS record does not contain record lengths (flags 0x{flags:x2}).");
        }

        var recordLength = reader.ReadUInt16LittleEndian();
        var headerLength = reader.ReadUInt16LittleEndian();
        return Create(recordLength, headerLength, reader.ReadBytes(recordLength), textEncoding, sourcePageOffset);
    }

    public static RawTpsRecord ReadNext(RawTpsRecord previous, TpsBinaryReader reader, Encoding textEncoding)
    {
        var flags = reader.ReadByte();
        var recordLength = (flags & 0x80) != 0 ? reader.ReadUInt16LittleEndian() : previous.RecordLength;
        var headerLength = (flags & 0x40) != 0 ? reader.ReadUInt16LittleEndian() : previous.HeaderLength;
        var prefixLength = flags & 0x3F;
        if (prefixLength > previous.Data.Length || prefixLength > recordLength)
        {
            throw new InvalidDataException($"Invalid TPS record prefix length {prefixLength} for record length {recordLength}.");
        }

        var data = new byte[recordLength];
        previous.Data.CopyPrefixTo(data, prefixLength);
        var remainder = reader.ReadBytes(recordLength - prefixLength);
        Array.Copy(remainder, 0, data, prefixLength, remainder.Length);
        return Create(recordLength, headerLength, data, textEncoding, previous.SourcePageOffset);
    }

    private static RawTpsRecord Create(
        int recordLength,
        int headerLength,
        byte[] data,
        Encoding textEncoding,
        int sourcePageOffset)
    {
        if (headerLength < 0 || headerLength > recordLength)
        {
            throw new InvalidDataException($"Invalid TPS record header length {headerLength} for record length {recordLength}.");
        }

        return new RawTpsRecord(recordLength, headerLength, data, textEncoding, sourcePageOffset);
    }

    private void BuildHeader(Encoding textEncoding)
    {
        var header = Data.ReadSlice(HeaderLength);
        if (header.Length == 0)
        {
            return;
        }

        if (header.PeekByte(0) == 0xFE)
        {
            Header = new TableNameHeader(header, textEncoding);
            return;
        }

        if (header.Length < 5)
        {
            return;
        }

        Header = header.PeekByte(4) switch
        {
            0xF3 => new DataHeader(header),
            0xFA => new TableDefinitionHeader(header),
            0xFC => new MemoHeader(header),
            _ => null
        };
    }
}

internal abstract class RecordHeader
{
    protected RecordHeader(TpsBinaryReader reader, byte expectedType, bool readTableNumber = true)
    {
        if (readTableNumber)
        {
            TableNumber = reader.ReadInt32BigEndian();
        }

        var type = reader.ReadByte();
        if (type != expectedType)
        {
            throw new InvalidDataException($"Expected TPS header type 0x{expectedType:x2}, found 0x{type:x2}.");
        }
    }

    public int TableNumber { get; }
}

internal sealed class DataHeader : RecordHeader
{
    public DataHeader(TpsBinaryReader reader)
        : base(reader, 0xF3)
    {
        RecordNumber = reader.ReadInt32BigEndian();
    }

    public int RecordNumber { get; }
}

internal sealed class TableDefinitionHeader : RecordHeader
{
    public TableDefinitionHeader(TpsBinaryReader reader)
        : base(reader, 0xFA)
    {
        BlockNumber = reader.ReadUInt16LittleEndian();
    }

    public int BlockNumber { get; }
}

internal sealed class TableNameHeader : RecordHeader
{
    public TableNameHeader(TpsBinaryReader reader, Encoding textEncoding)
        : base(reader, 0xFE, readTableNumber: false)
    {
        Name = reader.ReadFixedString(reader.Remaining, textEncoding);
    }

    public string Name { get; }
}

internal sealed class MemoHeader : RecordHeader
{
    public MemoHeader(TpsBinaryReader reader)
        : base(reader, 0xFC)
    {
        OwnerRecordNumber = reader.ReadInt32BigEndian();
        MemoIndex = reader.ReadByte();
        SequenceNumber = reader.ReadUInt16BigEndian();
    }

    public int OwnerRecordNumber { get; }
    public int MemoIndex { get; }
    public int SequenceNumber { get; }
}
