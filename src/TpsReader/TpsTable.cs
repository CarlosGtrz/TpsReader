namespace TpsReader;

/// <summary>Represents a table schema and any records materialized for it.</summary>
public sealed class TpsTable
{
    private Dictionary<int, TpsRecord>? _recordsByNumber;

    internal TpsTable(
        int tableNumber,
        string name,
        IReadOnlyList<TpsField> fields,
        IReadOnlyList<TpsMemo> memos,
        IReadOnlyList<TpsIndex> indexes,
        IReadOnlyList<TpsRecord> records,
        int recordLength = 0)
    {
        TableNumber = tableNumber;
        Name = name;
        Fields = fields;
        Memos = memos;
        Indexes = indexes;
        Records = records;
        RecordLength = recordLength;
    }

    /// <summary>Gets the table number stored by the TPS file.</summary>
    public int TableNumber { get; }

    /// <summary>Gets the resolved table name.</summary>
    public string Name { get; }

    /// <summary>Gets the declared ordinary fields.</summary>
    public IReadOnlyList<TpsField> Fields { get; }

    /// <summary>Gets the declared MEMO and BLOB definitions.</summary>
    public IReadOnlyList<TpsMemo> Memos { get; }

    /// <summary>Gets the declared indexes.</summary>
    public IReadOnlyList<TpsIndex> Indexes { get; }

    /// <summary>Gets materialized records in file order; streaming and metadata-only tables are empty.</summary>
    public IReadOnlyList<TpsRecord> Records { get; }

    /// <summary>Gets the record length declared by the table definition.</summary>
    public int RecordLength { get; }

    /// <summary>Gets a record by its TPS record number.</summary>
    public TpsRecord GetRecord(int recordNumber)
    {
        var recordsByNumber = Volatile.Read(ref _recordsByNumber);
        if (recordsByNumber is null)
        {
            var created = Records.ToDictionary(record => record.RecordNumber);
            recordsByNumber = Interlocked.CompareExchange(ref _recordsByNumber, created, null) ?? created;
        }

        if (recordsByNumber.TryGetValue(recordNumber, out var record))
        {
            return record;
        }

        throw new TpsParseException(new TpsParseError($"Record {recordNumber} was not found in table {TableNumber}."));
    }
}
