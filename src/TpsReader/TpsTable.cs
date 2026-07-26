namespace TpsReader;

/// <summary>Represents a table and its materialized schema and records.</summary>
public sealed class TpsTable
{
    private readonly Dictionary<int, TpsRecord> _recordsByNumber;

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
        _recordsByNumber = records.ToDictionary(r => r.RecordNumber);
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

    /// <summary>Gets the materialized records in file order.</summary>
    public IReadOnlyList<TpsRecord> Records { get; }

    /// <summary>Gets the record length declared by the table definition.</summary>
    public int RecordLength { get; }

    /// <summary>Gets a record by its TPS record number.</summary>
    public TpsRecord GetRecord(int recordNumber)
    {
        if (_recordsByNumber.TryGetValue(recordNumber, out var record))
        {
            return record;
        }

        throw new TpsParseException(new TpsParseError($"Record {recordNumber} was not found in table {TableNumber}."));
    }
}
