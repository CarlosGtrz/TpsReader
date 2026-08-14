using TpsReader.Internal;

namespace TpsReader;

/// <summary>
/// Represents a read-only TPS source whose records are decoded incrementally.
/// Dispose the instance after enumeration to release its source.
/// </summary>
public sealed class TpsStreamingFile : IDisposable
{
    private readonly ITpsRandomAccessSource _source;
    private readonly TpsFileReader _reader;
    private readonly TpsOpenOptions _options;
    private readonly IReadOnlyDictionary<int, TableDefinitionRecord> _definitions;
    private readonly Dictionary<int, TpsTable> _tablesByNumber;
    private readonly Dictionary<int, long> _recordCounts = [];
    private readonly string _inputDescription;
    private readonly string? _sourcePath;
    private bool _disposed;
    private int _operationActive;

    private TpsStreamingFile(
        ITpsRandomAccessSource source,
        TpsFileReader reader,
        TpsOpenOptions options,
        ParsedTpsMetadata metadata,
        IReadOnlyList<TpsTable> tables,
        string inputDescription,
        string? sourcePath)
    {
        _source = source;
        _reader = reader;
        _options = options;
        _definitions = metadata.Definitions;
        Tables = tables;
        _tablesByNumber = tables.ToDictionary(table => table.TableNumber);
        _inputDescription = inputDescription.ToLowerInvariant();
        _sourcePath = sourcePath;
    }

    /// <summary>Gets schema-only tables. Their Records collections are empty.</summary>
    public IReadOnlyList<TpsTable> Tables { get; }

    /// <summary>Gets whether owner-based decryption is being used.</summary>
    public bool IsEncrypted => _reader.IsEncrypted;

    /// <summary>Gets the cumulative number of malformed page incidents skipped by completed and active scans.</summary>
    public int RecoveryIssueCount => _reader.RecoveryIssueCount;

    internal static TpsStreamingFile Create(
        ITpsRandomAccessSource source,
        TpsOpenOptions options,
        string inputDescription,
        string? sourcePath)
    {
        var reader = new TpsFileReader(source, options.Owner, options.StringEncoding, options.Progress);
        var metadata = reader.ReadMetadata(options.IgnoreErrors);
        if (metadata.Definitions.Count == 0)
        {
            throw new TpsParseException(new TpsParseError(
                $"No table definitions were found in the TPS {inputDescription.ToLowerInvariant()}.",
                sourcePath));
        }

        var emptyData = metadata.Definitions.Keys.ToDictionary(
            key => key,
            _ => (IReadOnlyList<DataRecord>)Array.Empty<DataRecord>());
        var emptyMemos = new Dictionary<
            (int TableNumber, int MemoIndex),
            IReadOnlyDictionary<int, MemoRecord>>();
        var emptyContents = new ParsedTpsFile(metadata.Definitions, metadata.Names, emptyData, emptyMemos);
        var sourceTableName = metadata.Definitions.Count == 1
            ? TpsFile.GetSourceTableName(sourcePath)
            : null;
        var tables = metadata.Definitions
            .Select(definition => TpsFile.BuildTable(
                definition.Key,
                definition.Value,
                emptyContents,
                options,
                sourceTableName))
            .ToArray();

        return new TpsStreamingFile(
            source,
            reader,
            options,
            metadata,
            tables,
            inputDescription,
            sourcePath);
    }

    /// <summary>Gets the table when the source contains exactly one table.</summary>
    public TpsTable GetTable()
    {
        ThrowIfDisposed();
        if (Tables.Count == 1)
        {
            return Tables[0];
        }

        throw new TpsParseException(new TpsParseError(
            $"The TPS file contains {Tables.Count} tables; select one by name or number."));
    }

    /// <summary>Gets a table by its case-insensitive resolved name.</summary>
    public TpsTable GetTable(string name)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var matches = Tables
            .Where(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new TpsParseException(new TpsParseError($"Table '{name}' was not found.")),
            _ => throw new TpsParseException(new TpsParseError(
                $"Table name '{name}' is ambiguous; select the table by number."))
        };
    }

    /// <summary>Gets a table by its TPS table number.</summary>
    public TpsTable GetTable(int tableNumber)
    {
        ThrowIfDisposed();
        if (_tablesByNumber.TryGetValue(tableNumber, out var table))
        {
            return table;
        }

        throw new TpsParseException(new TpsParseError($"Table {tableNumber} was not found."));
    }

    /// <summary>Enumerates records in physical file order without retaining previous records.</summary>
    public IEnumerable<TpsRecord> ReadRecords(
        TpsTable table,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var state = ResolveTable(table);
        return WrapDeferredErrors(ReadRecordsIterator(table, state, cancellationToken));
    }

    /// <summary>Counts records with a bounded-memory scan and caches a successfully completed result.</summary>
    public long CountRecords(TpsTable table, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _ = ResolveTable(table);
        if (_recordCounts.TryGetValue(table.TableNumber, out var cached))
        {
            return cached;
        }

        using var operation = BeginOperation();
        try
        {
            var count = _reader.CountDataRecords(
                table.TableNumber,
                _options.IgnoreErrors,
                cancellationToken);
            _recordCounts[table.TableNumber] = count;
            return count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TpsParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateDeferredParseException(ex);
        }
    }

    /// <summary>Releases the underlying path handle or source wrapper.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.Dispose();
    }

    private IEnumerable<TpsRecord> ReadRecordsIterator(
        TpsTable table,
        TableDefinitionRecord definition,
        CancellationToken cancellationToken)
    {
        using var operation = BeginOperation();
        MemoLocationIndex? memoIndex = null;
        try
        {
            if (table.Memos.Count != 0)
            {
                memoIndex = _reader.BuildMemoIndex(
                    table.TableNumber,
                    _options.IgnoreErrors,
                    cancellationToken);
            }

            var layout = new TpsRecordLayout(table.Fields, table.Memos);

            long completedCount = 0;
            foreach (var dataRecord in _reader.ReadDataRecords(
                table.TableNumber,
                definition,
                _options.IgnoreErrors,
                TpsReadStage.StreamingRecords,
                cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = BuildRecord(
                    table,
                    dataRecord,
                    memoIndex,
                    layout,
                    cancellationToken);
                completedCount++;
                yield return record;
            }

            _recordCounts[table.TableNumber] = completedCount;
        }
        finally
        {
            // The operation lease is released even when enumeration stops early.
        }
    }

    private TpsRecord BuildRecord(
        TpsTable table,
        DataRecord record,
        MemoLocationIndex? memoIndex,
        TpsRecordLayout layout,
        CancellationToken cancellationToken)
    {
        var memoValues = new TpsMemoValue[table.Memos.Count];
        for (var i = 0; i < table.Memos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = table.Memos[i];
            var source = memoIndex is null
                ? null
                : _reader.ReadMemo(
                    memoIndex,
                    i,
                    record.RecordNumber,
                    _options.IgnoreErrors,
                    cancellationToken);
            memoValues[i] = BuildMemoValue(definition, source, record.RecordNumber);
        }

        return new TpsRecord(
            record.RecordNumber,
            record.Values,
            memoValues,
            layout,
            record.SourcePageOffset);
    }

    private TpsMemoValue BuildMemoValue(TpsMemo definition, MemoRecord? source, int recordNumber)
    {
        if (source is null)
        {
            return new TpsMemoValue(definition, null, null, TpsMemoState.Empty);
        }

        if (definition.IsMemo)
        {
            return new TpsMemoValue(
                definition,
                source.ReadText(_options.StringEncoding),
                null,
                source.FragmentState);
        }

        try
        {
            var blob = source.ReadBlob(_options.IgnoreErrors, out var state);
            return new TpsMemoValue(definition, null, blob, state);
        }
        catch (InvalidDataException ex)
        {
            throw new TpsParseException(new TpsParseError(
                $"Could not read BLOB '{definition.Name}' for record {recordNumber}: {ex.Message}",
                _sourcePath,
                ex));
        }
    }

    private TableDefinitionRecord ResolveTable(TpsTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!_tablesByNumber.TryGetValue(table.TableNumber, out var ownedTable) ||
            !ReferenceEquals(table, ownedTable))
        {
            throw new ArgumentException("The table does not belong to this streaming TPS file.", nameof(table));
        }

        return _definitions[table.TableNumber];
    }

    private IDisposable BeginOperation()
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Only one record enumeration or count operation may be active for a streaming TPS file.");
        }

        return new OperationLease(this);
    }

    private TpsParseException CreateDeferredParseException(Exception exception)
    {
        var pathSuffix = _sourcePath is null ? string.Empty : $" '{_sourcePath}'";
        return new TpsParseException(new TpsParseError(
            $"Could not read TPS {_inputDescription}{pathSuffix}: {exception.Message}",
            _sourcePath,
            exception));
    }

    private IEnumerable<TpsRecord> WrapDeferredErrors(IEnumerable<TpsRecord> source)
    {
        IEnumerator<TpsRecord> enumerator;
        try
        {
            enumerator = source.GetEnumerator();
        }
        catch (Exception ex) when (ShouldWrapDeferredException(ex))
        {
            throw CreateDeferredParseException(ex);
        }

        using (enumerator)
        {
            while (true)
            {
                TpsRecord current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex) when (ShouldWrapDeferredException(ex))
                {
                    throw CreateDeferredParseException(ex);
                }

                yield return current;
            }
        }
    }

    private static bool ShouldWrapDeferredException(Exception exception) =>
        exception is not TpsParseException and
        not OperationCanceledException and
        not InvalidOperationException and
        not ObjectDisposedException and
        not ArgumentException;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class OperationLease(TpsStreamingFile owner) : IDisposable
    {
        private TpsStreamingFile? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
            {
                Volatile.Write(ref current._operationActive, 0);
            }
        }
    }
}
