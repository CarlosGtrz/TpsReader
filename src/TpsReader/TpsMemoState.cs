namespace TpsReader;

/// <summary>Describes the availability and integrity of a record's MEMO or BLOB value.</summary>
public enum TpsMemoState
{
    /// <summary>No fragments exist for the value.</summary>
    Empty,
    /// <summary>All fragments and any BLOB length header are valid.</summary>
    Complete,
    /// <summary>The value is incomplete or has an invalid BLOB length header.</summary>
    Damaged
}
