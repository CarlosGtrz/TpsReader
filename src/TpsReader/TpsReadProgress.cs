namespace TpsReader;

/// <summary>Identifies a stage of opening or incrementally reading a TPS input.</summary>
public enum TpsReadStage
{
    /// <summary>The source bytes are being loaded.</summary>
    LoadingSource,
    /// <summary>The source header and blocks are being decrypted.</summary>
    DecryptingSource,
    /// <summary>Table definitions and names are being scanned.</summary>
    ScanningDefinitions,
    /// <summary>Records and MEMO/BLOB fragments are being scanned.</summary>
    ScanningRecordsAndMemos,
    /// <summary>MEMO/BLOB fragment locations are being indexed for streamed records.</summary>
    IndexingMemos,
    /// <summary>Records are being decoded and returned incrementally.</summary>
    StreamingRecords,
    /// <summary>Records are being counted without being materialized.</summary>
    CountingRecords
}

/// <summary>Reports byte-based progress while a TPS input is opened or scanned.</summary>
public sealed record TpsReadProgress(TpsReadStage Stage, long BytesCompleted, long BytesTotal);
