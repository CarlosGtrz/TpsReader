using System.Text;

namespace TpsReader.Internal;

internal sealed class TpsFileReader
{
    private readonly TpsRandomAccessReader _reader;
    private readonly Encoding _textEncoding;
    private readonly IProgress<TpsReadProgress>? _progress;
    private readonly Dictionary<TpsReadStage, long> _reportedProgress = [];

    public TpsFileReader(
        ITpsRandomAccessSource source,
        string? owner,
        Encoding textEncoding,
        IProgress<TpsReadProgress>? progress = null,
        int readAheadBufferBytes = 0)
    {
        _textEncoding = textEncoding;
        _progress = progress;
        _reader = new TpsRandomAccessReader(
            TpsRandomAccessSource.WithReadAhead(source, readAheadBufferBytes),
            owner);
        if (_reader.IsEncrypted)
        {
            ReportProgress(
                TpsReadStage.DecryptingSource,
                Math.Min(0x200, _reader.Length),
                _reader.Length);
        }
    }

    public TpsFileReader(
        byte[] data,
        string owner,
        Encoding textEncoding,
        bool ignoreErrors,
        IProgress<TpsReadProgress>? progress = null)
        : this(TpsRandomAccessSource.OpenBytes(data), owner, textEncoding, progress)
    {
        _ = ignoreErrors;
    }

    public TpsFileReader(
        byte[] data,
        Encoding textEncoding,
        IProgress<TpsReadProgress>? progress = null)
        : this(TpsRandomAccessSource.OpenBytes(data), null, textEncoding, progress)
    {
    }

    public bool IsEncrypted => _reader.IsEncrypted;
    public int RecoveryIssueCount { get; private set; }
    public int Length => _reader.Length;

    public TpsHeader GetHeader() => _reader.Header;

    public ParsedTpsFile Parse(bool ignoreErrors, bool metadataOnly = false)
    {
        var metadata = ReadMetadata(ignoreErrors);
        var (dataRecords, memoRecords) = metadataOnly
            ? CreateEmptyContent(metadata.Definitions)
            : ReadContent(metadata.Definitions, ignoreErrors);
        return new ParsedTpsFile(
            metadata.Definitions,
            metadata.Names,
            dataRecords,
            memoRecords);
    }

    internal ParsedTpsMetadata ReadMetadata(bool ignoreErrors)
    {
        var fragments = new SortedDictionary<int, List<RawTpsRecord?>>();
        var tableNames = new Dictionary<int, string>();

        foreach (var page in ReadRecordPages(ignoreErrors, TpsReadStage.ScanningDefinitions))
        {
            var pageDefinitions = new List<(TableDefinitionHeader Header, RawTpsRecord Record)>();
            var pageNames = new List<TableNameRecord>();
            try
            {
                foreach (var record in page.Records)
                {
                    switch (record.Header)
                    {
                        case TableDefinitionHeader header:
                            pageDefinitions.Add((header, record));
                            break;
                        case TableNameHeader:
                            pageNames.Add(new TableNameRecord(record));
                            break;
                    }
                }
            }
            catch (InvalidDataException) when (ignoreErrors)
            {
                RecoveryIssueCount++;
                continue;
            }

            foreach (var item in pageDefinitions)
            {
                AddFragment(fragments, item.Header.TableNumber, item.Header.BlockNumber, item.Record);
            }

            foreach (var tableName in pageNames)
            {
                tableNames.TryAdd(tableName.TableNumber, tableName.Name);
            }
        }

        var definitions = new SortedDictionary<int, TableDefinitionRecord>();
        foreach (var table in fragments)
        {
            if (!IsComplete(table.Value))
            {
                continue;
            }

            try
            {
                definitions[table.Key] = new TableDefinitionRecord(Merge(table.Value), _textEncoding);
            }
            catch (InvalidDataException) when (ignoreErrors)
            {
                RecoveryIssueCount++;
            }
        }

        return new ParsedTpsMetadata(definitions, tableNames);
    }

    internal IEnumerable<DataRecord> ReadDataRecords(
        int tableNumber,
        TableDefinitionRecord definition,
        bool ignoreErrors,
        TpsReadStage stage,
        CancellationToken cancellationToken = default)
    {
        foreach (var page in ReadRecordPages(ignoreErrors, stage, cancellationToken))
        {
            var pageRecords = new List<DataRecord>();
            try
            {
                foreach (var record in page.Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (record.Header is DataHeader header && header.TableNumber == tableNumber)
                    {
                        pageRecords.Add(new DataRecord(record, definition));
                    }
                }
            }
            catch (InvalidDataException) when (ignoreErrors)
            {
                RecoveryIssueCount++;
                continue;
            }

            foreach (var record in pageRecords)
            {
                yield return record;
            }
        }
    }

    internal long CountDataRecords(
        int tableNumber,
        bool ignoreErrors,
        CancellationToken cancellationToken = default)
    {
        long count = 0;
        foreach (var page in ReadRecordPages(ignoreErrors, TpsReadStage.CountingRecords, cancellationToken))
        {
            foreach (var record in page.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.Header is DataHeader header && header.TableNumber == tableNumber)
                {
                    count++;
                }
            }
        }

        return count;
    }

    internal MemoLocationIndex BuildMemoIndex(
        int tableNumber,
        bool ignoreErrors,
        CancellationToken cancellationToken = default)
    {
        var result = new MemoLocationIndex();
        foreach (var page in ReadRecordPages(ignoreErrors, TpsReadStage.IndexingMemos, cancellationToken))
        {
            for (var ordinal = 0; ordinal < page.Records.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page.Records[ordinal].Header is not MemoHeader header || header.TableNumber != tableNumber)
                {
                    continue;
                }

                result.Add(
                    header.MemoIndex,
                    header.OwnerRecordNumber,
                    header.SequenceNumber,
                    new MemoFragmentLocation(page.Page.SourceOffset, page.Page.PageSize, ordinal));
            }
        }

        return result;
    }

    internal MemoRecord? ReadMemo(
        MemoLocationIndex index,
        int memoIndex,
        int ownerRecordNumber,
        bool ignoreErrors,
        CancellationToken cancellationToken = default)
    {
        if (!index.TryGet(memoIndex, ownerRecordNumber, out var locations))
        {
            return null;
        }

        var isComplete = IsComplete(locations);
        if (!isComplete && !ignoreErrors)
        {
            throw new InvalidDataException(
                $"TPS MEMO/BLOB fragments are incomplete for memo {memoIndex}, record {ownerRecordNumber}.");
        }

        var fragments = new List<byte[]>();
        MemoHeader? firstHeader = null;
        var totalLength = 0;
        var currentPageOffset = -1;
        IReadOnlyList<RawTpsRecord>? currentPageRecords = null;
        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (location is null)
            {
                continue;
            }

            if (currentPageOffset != location.PageOffset)
            {
                currentPageOffset = location.PageOffset;
                currentPageRecords = ReadPageAt(location.PageOffset, location.PageSize)
                    .ReadRecords(_textEncoding);
            }

            if ((uint)location.RecordOrdinal >= (uint)currentPageRecords!.Count ||
                currentPageRecords[location.RecordOrdinal].Header is not MemoHeader header)
            {
                throw new InvalidDataException(
                    $"TPS MEMO/BLOB fragment at page {location.PageOffset} could not be located.");
            }

            firstHeader ??= header;
            var bytes = currentPageRecords[location.RecordOrdinal].Data.RemainingBytes();
            totalLength = checked(totalLength + bytes.Length);
            fragments.Add(bytes);
        }

        if (firstHeader is null)
        {
            return null;
        }

        var merged = new byte[totalLength];
        var destination = 0;
        foreach (var fragment in fragments)
        {
            fragment.CopyTo(merged, destination);
            destination += fragment.Length;
        }

        return new MemoRecord(
            firstHeader,
            new TpsBinaryReader(merged),
            isComplete ? TpsMemoState.Complete : TpsMemoState.Damaged);
    }

    private (
        Dictionary<int, IReadOnlyList<DataRecord>> DataRecords,
        Dictionary<(int TableNumber, int MemoIndex), IReadOnlyDictionary<int, MemoRecord>> MemoRecords)
        ReadContent(IReadOnlyDictionary<int, TableDefinitionRecord> definitions, bool ignoreErrors)
    {
        var dataByTable = definitions.Keys.ToDictionary(key => key, _ => new List<DataRecord>());
        var memoFragments = new Dictionary<(int TableNumber, int MemoIndex, int OwnerRecord), List<RawTpsRecord?>>();

        foreach (var page in ReadRecordPages(ignoreErrors, TpsReadStage.ScanningRecordsAndMemos))
        {
            var pageData = new List<(int TableNumber, DataRecord Record)>();
            var pageMemos = new List<(MemoHeader Header, RawTpsRecord Record)>();
            try
            {
                foreach (var record in page.Records)
                {
                    switch (record.Header)
                    {
                        case DataHeader dataHeader when definitions.TryGetValue(dataHeader.TableNumber, out var definition):
                            pageData.Add((dataHeader.TableNumber, new DataRecord(record, definition)));
                            break;
                        case MemoHeader memoHeader:
                            pageMemos.Add((memoHeader, record));
                            break;
                    }
                }
            }
            catch (InvalidDataException) when (ignoreErrors)
            {
                RecoveryIssueCount++;
                continue;
            }

            foreach (var item in pageData)
            {
                dataByTable[item.TableNumber].Add(item.Record);
            }

            foreach (var item in pageMemos)
            {
                var key = (item.Header.TableNumber, item.Header.MemoIndex, item.Header.OwnerRecordNumber);
                if (!memoFragments.TryGetValue(key, out var sequence))
                {
                    memoFragments[key] = sequence = [];
                }

                while (sequence.Count <= item.Header.SequenceNumber)
                {
                    sequence.Add(null);
                }

                sequence[item.Header.SequenceNumber] = item.Record;
            }
        }

        var memosByDefinition = new Dictionary<(int, int), Dictionary<int, MemoRecord>>();
        foreach (var group in memoFragments)
        {
            var isComplete = IsComplete(group.Value);
            if (!isComplete && !ignoreErrors)
            {
                throw new InvalidDataException(
                    $"TPS MEMO/BLOB fragments are incomplete for table {group.Key.TableNumber}, " +
                    $"memo {group.Key.MemoIndex}, record {group.Key.OwnerRecord}.");
            }

            var firstRecord = group.Value.First(record => record is not null)!;
            var firstHeader = (MemoHeader)firstRecord.Header!;
            var memo = new MemoRecord(
                firstHeader,
                isComplete ? Merge(group.Value) : MergeAvailable(group.Value),
                isComplete ? TpsMemoState.Complete : TpsMemoState.Damaged);
            var definitionKey = (group.Key.TableNumber, group.Key.MemoIndex);
            if (!memosByDefinition.TryGetValue(definitionKey, out var records))
            {
                memosByDefinition[definitionKey] = records = [];
            }

            records[memo.OwnerRecordNumber] = memo;
        }

        return (
            dataByTable.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<DataRecord>)pair.Value),
            memosByDefinition.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<int, MemoRecord>)pair.Value));
    }

    private IEnumerable<DecodedPage> ReadRecordPages(
        bool ignoreErrors,
        TpsReadStage stage,
        CancellationToken cancellationToken = default)
    {
        ResetProgress(stage);
        ReportProgress(stage, 0, _reader.Length);
        foreach (var page in ReadPages(ignoreErrors, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadPage(
                page,
                _textEncoding,
                ignoreErrors,
                out var records,
                ReportRecoveryIssue))
            {
                yield return new DecodedPage(page, records);
            }

            ReportProgress(stage, page.EndOffset, _reader.Length);
        }

        ReportProgress(stage, _reader.Length, _reader.Length);
    }

    internal static bool TryReadPage(
        TpsPage page,
        Encoding textEncoding,
        bool ignoreErrors,
        out IReadOnlyList<RawTpsRecord> records,
        Action? reportRecoveryIssue = null)
    {
        try
        {
            records = page.ReadRecords(textEncoding);
            return true;
        }
        catch (InvalidDataException) when (ignoreErrors)
        {
            reportRecoveryIssue?.Invoke();
            records = [];
            return false;
        }
    }

    private IEnumerable<TpsPage> ReadPages(bool ignoreErrors, CancellationToken cancellationToken)
    {
        var header = GetHeader();
        for (var i = 0; i < header.PageStarts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = header.PageStarts[i];
            var end = header.PageEnds[i];
            if (IsEmptyBlock(start, end) || start >= _reader.Length)
            {
                continue;
            }

            try
            {
                ValidateBlockRange(start, end);
            }
            catch (InvalidDataException) when (ignoreErrors)
            {
                RecoveryIssueCount++;
                continue;
            }

            var position = start;
            while (position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TpsPage? decodedPage = null;
                try
                {
                    var pageSize = GetCompletePageSize(position, end);
                    if (pageSize is not null)
                    {
                        decodedPage = ReadPageAt(position, pageSize.Value);
                        position = decodedPage.EndOffset;
                    }
                    else
                    {
                        position = AdvanceOneSector(position, end);
                    }
                }
                catch (InvalidDataException) when (ignoreErrors)
                {
                    RecoveryIssueCount++;
                    position = AdvanceOneSector(position, end);
                }

                if (decodedPage is not null)
                {
                    yield return decodedPage;
                }

                position = NavigateToNextPage(position, end);
            }
        }
    }

    private int? GetCompletePageSize(int pageStart, int blockEnd)
    {
        var prefix = _reader.ReadBytes(pageStart, 6);
        var prefixReader = new TpsBinaryReader(prefix);
        _ = prefixReader.ReadInt32LittleEndian();
        var pageSize = prefixReader.ReadUInt16LittleEndian();
        if (pageSize < 13 || pageStart + pageSize > blockEnd)
        {
            throw new InvalidDataException($"Invalid TPS page size {pageSize} at offset {pageStart}.");
        }

        for (var offset = 0x100; offset < pageSize; offset += 0x100)
        {
            if (offset > blockEnd - pageStart - 4)
            {
                return null;
            }

            var sectorPosition = pageStart + offset;
            var sector = _reader.ReadBytes(sectorPosition, Math.Min(6, blockEnd - sectorPosition));
            var sectorReader = new TpsBinaryReader(sector);
            if (sectorReader.ReadInt32LittleEndian() == sectorPosition && sector.Length >= 6)
            {
                var nestedPageSize = sectorReader.ReadUInt16LittleEndian();
                if (nestedPageSize >= 13 &&
                    nestedPageSize <= blockEnd - sectorPosition &&
                    nestedPageSize <= _reader.Length - sectorPosition)
                {
                    return null;
                }
            }
        }

        return pageSize;
    }

    private TpsPage ReadPageAt(int pageOffset, int pageSize)
    {
        var bytes = _reader.ReadBytes(pageOffset, pageSize);
        return new TpsPage(new TpsBinaryReader(bytes), pageOffset);
    }

    private int NavigateToNextPage(int position, int end)
    {
        if ((position & 0xFF) != 0)
        {
            position = Math.Min(end, (position & ~0xFF) + 0x100);
        }

        while (position < end && end - position >= 4)
        {
            var address = new TpsBinaryReader(_reader.ReadBytes(position, 4)).ReadInt32LittleEndian();
            if (address == position)
            {
                break;
            }

            position = Math.Min(end, position + 0x100);
        }

        return position;
    }

    private static int AdvanceOneSector(int position, int end)
    {
        var next = Math.Min(end, (position & ~0xFF) + 0x100);
        return next <= position ? Math.Min(end, position + 0x100) : next;
    }

    private void ValidateBlockRange(int start, int end)
    {
        if (start < 0x200 || end < start || end > _reader.Length)
        {
            throw new InvalidDataException($"Invalid TPS block range: start={start}, end={end}, file={_reader.Length}.");
        }
    }

    private static void AddFragment(
        SortedDictionary<int, List<RawTpsRecord?>> fragments,
        int group,
        int sequenceNumber,
        RawTpsRecord record)
    {
        if (!fragments.TryGetValue(group, out var sequence))
        {
            fragments[group] = sequence = [];
        }

        while (sequence.Count <= sequenceNumber)
        {
            sequence.Add(null);
        }

        sequence[sequenceNumber] = record;
    }

    private static bool IsComplete<T>(IEnumerable<T?> records) where T : class =>
        records.All(record => record is not null);

    private static TpsBinaryReader Merge(IEnumerable<RawTpsRecord?> records)
    {
        using var output = new MemoryStream();
        foreach (var record in records)
        {
            output.Write(record!.Data.RemainingBytes());
        }

        return new TpsBinaryReader(output.ToArray());
    }

    private static TpsBinaryReader MergeAvailable(IEnumerable<RawTpsRecord?> records)
    {
        using var output = new MemoryStream();
        foreach (var record in records)
        {
            if (record is not null)
            {
                output.Write(record.Data.RemainingBytes());
            }
        }

        return new TpsBinaryReader(output.ToArray());
    }

    private static (
        Dictionary<int, IReadOnlyList<DataRecord>> DataRecords,
        Dictionary<(int TableNumber, int MemoIndex), IReadOnlyDictionary<int, MemoRecord>> MemoRecords)
        CreateEmptyContent(IReadOnlyDictionary<int, TableDefinitionRecord> definitions) =>
        (
            definitions.Keys.ToDictionary(key => key, _ => (IReadOnlyList<DataRecord>)Array.Empty<DataRecord>()),
            []);

    private void ReportRecoveryIssue() => RecoveryIssueCount++;

    private void ResetProgress(TpsReadStage stage) => _reportedProgress.Remove(stage);

    private void ReportProgress(TpsReadStage stage, long completed, long total)
    {
        if (_progress is null)
        {
            return;
        }

        total = Math.Max(1, total);
        completed = Math.Clamp(completed, 0, total);
        completed = Math.Max(completed, _reportedProgress.GetValueOrDefault(stage));
        _reportedProgress[stage] = completed;
        _progress.Report(new TpsReadProgress(stage, completed, total));
    }

    private static bool IsEmptyBlock(int start, int end) => start == 0x200 && end == 0x200;

    internal static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return ReadAllBytes(stream);
    }

    internal static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.CanSeek)
        {
            var remainingLength = Math.Max(0, stream.Length - stream.Position);
            if (remainingLength > int.MaxValue)
            {
                throw InputTooLarge();
            }

            var data = new byte[(int)remainingLength];
            stream.ReadExactly(data);
            return data;
        }

        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                return output.ToArray();
            }

            if (output.Length > int.MaxValue - bytesRead)
            {
                throw InputTooLarge();
            }

            output.Write(buffer, 0, bytesRead);
        }
    }

    private static NotSupportedException InputTooLarge() =>
        new($"TPS inputs larger than {int.MaxValue} bytes are not supported.");

    private sealed record DecodedPage(TpsPage Page, IReadOnlyList<RawTpsRecord> Records);
}

internal sealed class MemoLocationIndex
{
    private readonly Dictionary<(int MemoIndex, int OwnerRecord), List<MemoFragmentLocation?>> _locations = [];

    public void Add(int memoIndex, int ownerRecord, int sequenceNumber, MemoFragmentLocation location)
    {
        var key = (memoIndex, ownerRecord);
        if (!_locations.TryGetValue(key, out var sequence))
        {
            _locations[key] = sequence = [];
        }

        while (sequence.Count <= sequenceNumber)
        {
            sequence.Add(null);
        }

        sequence[sequenceNumber] = location;
    }

    public bool TryGet(
        int memoIndex,
        int ownerRecord,
        out IReadOnlyList<MemoFragmentLocation?> locations)
    {
        if (_locations.TryGetValue((memoIndex, ownerRecord), out var found))
        {
            locations = found;
            return true;
        }

        locations = [];
        return false;
    }
}

internal sealed record MemoFragmentLocation(int PageOffset, int PageSize, int RecordOrdinal);

internal sealed record ParsedTpsMetadata(
    IReadOnlyDictionary<int, TableDefinitionRecord> Definitions,
    IReadOnlyDictionary<int, string> Names);

internal sealed record ParsedTpsFile(
    IReadOnlyDictionary<int, TableDefinitionRecord> TableDefinitions,
    IReadOnlyDictionary<int, string> TableNames,
    IReadOnlyDictionary<int, IReadOnlyList<DataRecord>> DataRecords,
    IReadOnlyDictionary<(int TableNumber, int MemoIndex), IReadOnlyDictionary<int, MemoRecord>> MemoRecords);
