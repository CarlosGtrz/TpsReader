namespace TpsReader;

/// <summary>Identifies a stage of opening a TPS input.</summary>
public enum TpsReadStage
{
    /// <summary>The source bytes are being loaded.</summary>
    LoadingSource,
    /// <summary>The source header and blocks are being decrypted.</summary>
    DecryptingSource,
    /// <summary>Table definitions and names are being scanned.</summary>
    ScanningDefinitions,
    /// <summary>Records and MEMO/BLOB fragments are being scanned.</summary>
    ScanningRecordsAndMemos
}

/// <summary>Reports byte-based progress while a TPS input is opened.</summary>
public sealed record TpsReadProgress(TpsReadStage Stage, long BytesCompleted, long BytesTotal);
