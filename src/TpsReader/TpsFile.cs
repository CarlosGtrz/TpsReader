using System.Globalization;
using TpsReader.Internal;

namespace TpsReader;

/// <summary>Represents a read-only parsed TPS file.</summary>
public sealed class TpsFile
{
    private const string FormatName = "TPS";
    private readonly Dictionary<int, TpsTable> _tablesByNumber;

    private enum InputKind
    {
        File,
        Stream,
        ByteArray
    }

    internal TpsFile(
        IReadOnlyList<TpsTable> tables,
        bool isMetadataOnly = false,
        bool isEncrypted = false,
        int recoveryIssueCount = 0)
    {
        Tables = tables;
        IsMetadataOnly = isMetadataOnly;
        IsEncrypted = isEncrypted;
        RecoveryIssueCount = recoveryIssueCount;
        _tablesByNumber = tables.ToDictionary(table => table.TableNumber);
    }

    /// <summary>Gets all tables discovered in the file.</summary>
    public IReadOnlyList<TpsTable> Tables { get; }

    /// <summary>Gets whether this instance contains definitions without materialized records.</summary>
    public bool IsMetadataOnly { get; }

    /// <summary>Gets whether owner-based decryption was used to open the input.</summary>
    public bool IsEncrypted { get; }

    /// <summary>Gets the number of malformed block or page incidents skipped during recovery.</summary>
    public int RecoveryIssueCount { get; }

    /// <summary>Gets the table when the file contains exactly one table.</summary>
    public TpsTable GetTable()
    {
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

    /// <summary>Opens and parses a TPS file from a path.</summary>
    public static TpsFile Open(string path, TpsOpenOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options = ValidateOptions(options);

        try
        {
            using var source = TpsRandomAccessSource.OpenPath(path);
            ReportLoading(options, source.Length);
            return Parse(source, options, InputKind.File, path, metadataOnly: false);
        }
        catch (TpsParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateOpenException(InputKind.File, ex, path);
        }
    }

    /// <summary>Opens and parses TPS data from the stream's current position without disposing it.</summary>
    public static TpsFile Open(Stream stream, TpsOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        options = ValidateOptions(options);

        try
        {
            if (!stream.CanSeek)
            {
                var data = TpsFileReader.ReadAllBytes(stream);
                ReportLoading(options, data.Length);
                using var bufferedSource = TpsRandomAccessSource.OpenBytes(data);
                return Parse(bufferedSource, options, InputKind.Stream, sourcePath: null, metadataOnly: false);
            }

            using var source = TpsRandomAccessSource.OpenStream(stream);
            ReportLoading(options, source.Length);
            return Parse(source, options, InputKind.Stream, sourcePath: null, metadataOnly: false);
        }
        catch (TpsParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateOpenException(InputKind.Stream, ex);
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = stream.Length;
            }
        }
    }

    /// <summary>Opens and parses a complete TPS byte array without retaining or modifying it.</summary>
    public static TpsFile Open(byte[] data, TpsOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options = ValidateOptions(options);

        try
        {
            using var source = TpsRandomAccessSource.OpenBytes(data);
            ReportLoading(options, source.Length);
            return Parse(source, options, InputKind.ByteArray, sourcePath: null, metadataOnly: false);
        }
        catch (TpsParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateOpenException(InputKind.ByteArray, ex);
        }
    }

    /// <summary>Opens a TPS path and parses definitions without materializing records or MEMO/BLOB data.</summary>
    public static TpsFile OpenMetadata(string path, TpsOpenOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options = ValidateOptions(options);

        try
        {
            using var source = TpsRandomAccessSource.OpenPath(path);
            ReportLoading(options, source.Length);
            return Parse(source, options, InputKind.File, path, metadataOnly: true);
        }
        catch (TpsParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateOpenException(InputKind.File, ex, path);
        }
    }

    /// <summary>Opens TPS data from a stream and parses definitions without materializing content.</summary>
    public static TpsFile OpenMetadata(Stream stream, TpsOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        options = ValidateOptions(options);

        try
        {
            if (!stream.CanSeek)
            {
                var data = TpsFileReader.ReadAllBytes(stream);
                ReportLoading(options, data.Length);
                using var bufferedSource = TpsRandomAccessSource.OpenBytes(data);
                return Parse(bufferedSource, options, InputKind.Stream, sourcePath: null, metadataOnly: true);
            }

            using var source = TpsRandomAccessSource.OpenStream(stream);
            ReportLoading(options, source.Length);
            return Parse(source, options, InputKind.Stream, sourcePath: null, metadataOnly: true);
        }
        catch (TpsParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateOpenException(InputKind.Stream, ex);
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = stream.Length;
            }
        }
    }

    /// <summary>Opens TPS bytes and parses definitions without materializing content.</summary>
    public static TpsFile OpenMetadata(byte[] data, TpsOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options = ValidateOptions(options);

        try
        {
            using var source = TpsRandomAccessSource.OpenBytes(data);
            ReportLoading(options, source.Length);
            return Parse(source, options, InputKind.ByteArray, sourcePath: null, metadataOnly: true);
        }
        catch (TpsParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateOpenException(InputKind.ByteArray, ex);
        }
    }

    /// <summary>Opens a TPS path for bounded-memory, synchronous record enumeration.</summary>
    public static TpsStreamingFile OpenStreaming(string path, TpsOpenOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options = ValidateOptions(options);
        ITpsRandomAccessSource? source = null;
        try
        {
            source = TpsRandomAccessSource.OpenPath(path);
            var result = TpsStreamingFile.Create(source, options, "file", path);
            source = null;
            return result;
        }
        catch (TpsParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateOpenException(InputKind.File, ex, path);
        }
        finally
        {
            source?.Dispose();
        }
    }

    /// <summary>Opens a seekable TPS stream for bounded-memory, synchronous record enumeration.</summary>
    public static TpsStreamingFile OpenStreaming(Stream stream, TpsOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        if (!stream.CanSeek)
        {
            throw new NotSupportedException(
                "Streaming TPS reads require a seekable stream. Extract or spool the TPS input to a file first.");
        }

        options = ValidateOptions(options);
        ITpsRandomAccessSource? source = null;
        try
        {
            source = TpsRandomAccessSource.OpenStream(stream);
            var result = TpsStreamingFile.Create(source, options, "stream", sourcePath: null);
            source = null;
            return result;
        }
        catch (TpsParseException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateOpenException(InputKind.Stream, ex);
        }
        finally
        {
            source?.Dispose();
        }
    }

    /// <summary>Opens complete TPS bytes for bounded-memory, synchronous record enumeration.</summary>
    public static TpsStreamingFile OpenStreaming(byte[] data, TpsOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options = ValidateOptions(options);
        ITpsRandomAccessSource? source = null;
        try
        {
            source = TpsRandomAccessSource.OpenBytes(data);
            var result = TpsStreamingFile.Create(source, options, "byte array", sourcePath: null);
            source = null;
            return result;
        }
        catch (TpsParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateOpenException(InputKind.ByteArray, ex);
        }
        finally
        {
            source?.Dispose();
        }
    }

    /// <summary>Attempts to open a TPS path for bounded-memory record enumeration.</summary>
    public static bool TryOpenStreaming(
        string path,
        out TpsStreamingFile? file,
        out TpsParseError? error,
        TpsOpenOptions? options = null) =>
        TryOpenStreamingCore(() => OpenStreaming(path, options), out file, out error);

    /// <summary>Attempts to open a seekable TPS stream for bounded-memory record enumeration.</summary>
    public static bool TryOpenStreaming(
        Stream stream,
        out TpsStreamingFile? file,
        out TpsParseError? error,
        TpsOpenOptions? options = null) =>
        TryOpenStreamingCore(() => OpenStreaming(stream, options), out file, out error);

    /// <summary>Attempts to open complete TPS bytes for bounded-memory record enumeration.</summary>
    public static bool TryOpenStreaming(
        byte[] data,
        out TpsStreamingFile? file,
        out TpsParseError? error,
        TpsOpenOptions? options = null) =>
        TryOpenStreamingCore(() => OpenStreaming(data, options), out file, out error);

    /// <summary>Attempts to open a TPS file path and returns a structured error on failure.</summary>
    public static bool TryOpen(
        string path,
        out TpsFile? file,
        out TpsParseError? error,
        TpsOpenOptions? options = null)
    {
        try
        {
            file = Open(path, options);
            error = null;
            return true;
        }
        catch (TpsParseException ex)
        {
            file = null;
            error = ex.Error;
            return false;
        }
    }

    /// <summary>Attempts to open TPS data from a stream and returns a structured error on failure.</summary>
    public static bool TryOpen(
        Stream stream,
        out TpsFile? file,
        out TpsParseError? error,
        TpsOpenOptions? options = null)
    {
        try
        {
            file = Open(stream, options);
            error = null;
            return true;
        }
        catch (TpsParseException ex)
        {
            file = null;
            error = ex.Error;
            return false;
        }
    }

    /// <summary>Attempts to open a complete TPS byte array and returns a structured error on failure.</summary>
    public static bool TryOpen(
        byte[] data,
        out TpsFile? file,
        out TpsParseError? error,
        TpsOpenOptions? options = null)
    {
        try
        {
            file = Open(data, options);
            error = null;
            return true;
        }
        catch (TpsParseException ex)
        {
            file = null;
            error = ex.Error;
            return false;
        }
    }

    /// <summary>Gets a table by its TPS table number.</summary>
    public TpsTable GetTable(int tableNumber)
    {
        if (_tablesByNumber.TryGetValue(tableNumber, out var table))
        {
            return table;
        }

        throw new TpsParseException(new TpsParseError($"Table {tableNumber} was not found."));
    }

    private static TpsFile Parse(
        ITpsRandomAccessSource source,
        TpsOpenOptions options,
        InputKind inputKind,
        string? sourcePath,
        bool metadataOnly)
    {
        using var streaming = TpsStreamingFile.Create(
            source,
            options,
            inputKind switch
            {
                InputKind.File => "file",
                InputKind.Stream => "stream",
                InputKind.ByteArray => "byte array",
                _ => throw new ArgumentOutOfRangeException(nameof(inputKind))
            },
            sourcePath);
        if (metadataOnly)
        {
            return new TpsFile(
                streaming.Tables,
                isMetadataOnly: true,
                streaming.IsEncrypted,
                streaming.RecoveryIssueCount);
        }

        var tables = new List<TpsTable>(streaming.Tables.Count);
        options.Progress?.Report(new TpsReadProgress(
            TpsReadStage.ScanningRecordsAndMemos,
            0,
            Math.Max(1, source.Length)));
        foreach (var table in streaming.Tables)
        {
            var records = streaming.ReadRecords(table).ToArray();
            tables.Add(new TpsTable(
                table.TableNumber,
                table.Name,
                table.Fields,
                table.Memos,
                table.Indexes,
                records,
                table.RecordLength));
        }
        options.Progress?.Report(new TpsReadProgress(
            TpsReadStage.ScanningRecordsAndMemos,
            source.Length,
            Math.Max(1, source.Length)));

        return new TpsFile(
            tables,
            isMetadataOnly: false,
            streaming.IsEncrypted,
            streaming.RecoveryIssueCount);
    }

    private static TpsParseException CreateOpenException(
        InputKind inputKind,
        Exception exception,
        string? sourcePath = null)
    {
        var pathSuffix = sourcePath is null ? string.Empty : $" '{sourcePath}'";
        return new TpsParseException(new TpsParseError(
            $"Could not open or parse {Describe(inputKind)}{pathSuffix}: {exception.Message}",
            sourcePath,
            exception));
    }

    private static string Describe(InputKind inputKind)
    {
        var inputName = inputKind switch
        {
            InputKind.File => "file",
            InputKind.Stream => "stream",
            InputKind.ByteArray => "byte array",
            _ => throw new ArgumentOutOfRangeException(nameof(inputKind))
        };

        return $"{FormatName} {inputName}";
    }

    internal static TpsOpenOptions ValidateOptions(TpsOpenOptions? options)
    {
        options ??= new TpsOpenOptions();
        ArgumentNullException.ThrowIfNull(options.StringEncoding);
        return options;
    }

    internal static TpsTable BuildTable(
        int tableNumber,
        TableDefinitionRecord definition,
        ParsedTpsFile contents,
        TpsOpenOptions options,
        string? sourceTableName)
    {
        var fields = definition.Fields
            .Select((field, index) => new TpsField(
                index + 1,
                field.Name,
                field.ShortName,
                field.TablePrefix,
                MapFieldType(field.FieldType),
                field.Offset,
                field.Length,
                field.ElementCount,
                field.DecimalDigits,
                field.DecimalStorageLength,
                field.FieldType,
                field.Flags,
                field.IndexNumber,
                field.StringLength,
                field.StringMask))
            .ToArray();

        var memos = definition.Memos
            .Select((memo, index) => new TpsMemo(
                index + 1,
                memo.Name,
                memo.ShortName,
                memo.Flags,
                memo.IsBlob,
                memo.Length,
                memo.ExternalName))
            .ToArray();

        var indexes = definition.Indexes
            .Select((index, ordinal) => new TpsIndex(
                ordinal + 1,
                index.Name,
                index.FieldCount,
                index.Flags,
                index.ExternalName,
                index.Components
                    .Select(component => new TpsIndexComponent(
                        component.Rank,
                        component.FieldIndex,
                        component.Flags))
                    .ToArray()))
            .ToArray();

        var ambiguousFieldAliases = FindAmbiguousAliases(fields, field => field.Name, field => field.ShortName);
        var ambiguousMemoAliases = FindAmbiguousAliases(memos, memo => memo.Name, memo => memo.ShortName);
        var sourceRecords = contents.DataRecords.GetValueOrDefault(tableNumber) ?? [];
        var records = sourceRecords
            .Select(record => BuildRecord(
                tableNumber,
                record,
                fields,
                memos,
                contents.MemoRecords,
                ambiguousFieldAliases,
                ambiguousMemoAliases,
                options))
            .ToArray();

        var name = ResolveTableName(tableNumber, definition, contents.TableNames, sourceTableName);
        return new TpsTable(
            tableNumber,
            name,
            fields,
            memos,
            indexes,
            records,
            definition.RecordLength);
    }

    private static TpsRecord BuildRecord(
        int tableNumber,
        DataRecord record,
        TpsField[] fields,
        TpsMemo[] memos,
        IReadOnlyDictionary<(int TableNumber, int MemoIndex), IReadOnlyDictionary<int, MemoRecord>> memoRecords,
        IReadOnlySet<string> ambiguousFieldAliases,
        IReadOnlySet<string> ambiguousMemoAliases,
        TpsOpenOptions options)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var fieldTypes = new Dictionary<string, TpsFieldType>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fields.Length; i++)
        {
            AddAliases(values, fields[i].Name, fields[i].ShortName, record.Values[i], ambiguousFieldAliases);
            AddAliases(fieldTypes, fields[i].Name, fields[i].ShortName, fields[i].Type, ambiguousFieldAliases);
        }

        var memoValues = new Dictionary<string, TpsMemoValue>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < memos.Length; i++)
        {
            var memoDefinition = memos[i];
            var records = memoRecords.GetValueOrDefault((tableNumber, i));
            var source = records?.GetValueOrDefault(record.RecordNumber);
            var value = BuildMemoValue(memoDefinition, source, record.RecordNumber, options);
            AddAliases(memoValues, memoDefinition.Name, memoDefinition.ShortName, value, ambiguousMemoAliases);
        }

        return new TpsRecord(
            record.RecordNumber,
            values,
            memoValues,
            ambiguousFieldAliases,
            ambiguousMemoAliases,
            fieldTypes,
            record.SourcePageOffset);
    }

    private static TpsMemoValue BuildMemoValue(
        TpsMemo definition,
        MemoRecord? source,
        int recordNumber,
        TpsOpenOptions options)
    {
        if (source is null)
        {
            return new TpsMemoValue(definition, null, null, TpsMemoState.Empty);
        }

        if (definition.IsMemo)
        {
            return new TpsMemoValue(
                definition,
                source.ReadText(options.StringEncoding),
                null,
                source.FragmentState);
        }

        try
        {
            var blob = source.ReadBlob(options.IgnoreErrors, out var state);
            return new TpsMemoValue(definition, null, blob, state);
        }
        catch (InvalidDataException ex)
        {
            throw new TpsParseException(new TpsParseError(
                $"Could not read BLOB '{definition.Name}' for record {recordNumber}: {ex.Message}",
                Exception: ex));
        }
    }

    internal static HashSet<string> FindAmbiguousAliases<T>(
        IEnumerable<T> definitions,
        Func<T, string> getName,
        Func<T, string> getShortName)
    {
        var items = definitions.ToArray();
        var fullNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!fullNames.Add(getName(item)))
            {
                throw new InvalidDataException($"Duplicate TPS field or MEMO name '{getName(item)}'.");
            }
        }

        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var shortName = getShortName(item);
            if (string.IsNullOrWhiteSpace(shortName) || string.Equals(shortName, getName(item), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            aliases[shortName] = aliases.GetValueOrDefault(shortName) + 1;
        }

        return aliases
            .Where(alias => alias.Value > 1 || fullNames.Contains(alias.Key))
            .Select(alias => alias.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static void AddAliases<T>(
        IDictionary<string, T> values,
        string name,
        string shortName,
        T value,
        IReadOnlySet<string> ambiguousAliases)
    {
        values.Add(name, value);
        if (!string.IsNullOrWhiteSpace(shortName) && !ambiguousAliases.Contains(shortName))
        {
            values.TryAdd(shortName, value);
        }
    }

    private static string ResolveTableName(
        int tableNumber,
        TableDefinitionRecord definition,
        IReadOnlyDictionary<int, string> tableNames,
        string? sourceTableName)
    {
        if (tableNames.TryGetValue(tableNumber, out var tableName))
        {
            var normalizedName = tableName.TrimEnd('\0', ' ');
            if (!string.IsNullOrWhiteSpace(normalizedName) &&
                !string.Equals(normalizedName, "UNNAMED", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedName;
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceTableName))
        {
            return sourceTableName;
        }

        var fieldPrefix = definition.Fields.FirstOrDefault()?.TablePrefix;
        return string.IsNullOrWhiteSpace(fieldPrefix)
            ? tableNumber.ToString(CultureInfo.InvariantCulture)
            : fieldPrefix;
    }

    internal static string? GetSourceTableName(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var sourceTableName = Path.GetFileNameWithoutExtension(sourcePath).TrimEnd();
        return string.IsNullOrWhiteSpace(sourceTableName) ? null : sourceTableName;
    }

    private static TpsFieldType MapFieldType(int type) => type switch
    {
        TpsFormatConstants.FieldByte => TpsFieldType.Byte,
        TpsFormatConstants.FieldShort => TpsFieldType.Short,
        TpsFormatConstants.FieldUShort => TpsFieldType.UShort,
        TpsFormatConstants.FieldDate => TpsFieldType.Date,
        TpsFormatConstants.FieldTime => TpsFieldType.Time,
        TpsFormatConstants.FieldLong => TpsFieldType.Long,
        TpsFormatConstants.FieldULong => TpsFieldType.ULong,
        TpsFormatConstants.FieldSReal => TpsFieldType.SReal,
        TpsFormatConstants.FieldReal => TpsFieldType.Real,
        TpsFormatConstants.FieldDecimal => TpsFieldType.Decimal,
        TpsFormatConstants.FieldString => TpsFieldType.String,
        TpsFormatConstants.FieldCString => TpsFieldType.CString,
        TpsFormatConstants.FieldPString => TpsFieldType.PString,
        TpsFormatConstants.FieldGroup => TpsFieldType.Group,
        _ => TpsFieldType.Unknown
    };

    private static void ReportLoading(TpsOpenOptions options, int length)
    {
        options.Progress?.Report(new TpsReadProgress(
            TpsReadStage.LoadingSource,
            length,
            Math.Max(1, length)));
    }

    private static bool TryOpenStreamingCore(
        Func<TpsStreamingFile> open,
        out TpsStreamingFile? file,
        out TpsParseError? error)
    {
        try
        {
            file = open();
            error = null;
            return true;
        }
        catch (TpsParseException ex)
        {
            file = null;
            error = ex.Error;
            return false;
        }
    }
}
