namespace TpsReader;

/// <summary>Exposes raw values used by the TPS schema format.</summary>
public static class TpsFormatConstants
{
    /// <summary>Raw BYTE field code.</summary>
    public const int FieldByte = 0x01;
    /// <summary>Raw SHORT field code.</summary>
    public const int FieldShort = 0x02;
    /// <summary>Raw USHORT field code.</summary>
    public const int FieldUShort = 0x03;
    /// <summary>Raw DATE field code.</summary>
    public const int FieldDate = 0x04;
    /// <summary>Raw TIME field code.</summary>
    public const int FieldTime = 0x05;
    /// <summary>Raw LONG field code.</summary>
    public const int FieldLong = 0x06;
    /// <summary>Raw ULONG field code.</summary>
    public const int FieldULong = 0x07;
    /// <summary>Raw SREAL field code.</summary>
    public const int FieldSReal = 0x08;
    /// <summary>Raw REAL field code.</summary>
    public const int FieldReal = 0x09;
    /// <summary>Raw DECIMAL field code.</summary>
    public const int FieldDecimal = 0x0A;
    /// <summary>Raw STRING field code.</summary>
    public const int FieldString = 0x12;
    /// <summary>Raw CSTRING field code.</summary>
    public const int FieldCString = 0x13;
    /// <summary>Raw PSTRING field code.</summary>
    public const int FieldPString = 0x14;
    /// <summary>Raw GROUP field code.</summary>
    public const int FieldGroup = 0x16;
    /// <summary>Raw MEMO/BLOB field code.</summary>
    public const int MemoField = 0xFC;

    /// <summary>MEMO flag indicating binary storage.</summary>
    public const int MemoBinaryFlag = 0x02;
    /// <summary>MEMO flag indicating BLOB content.</summary>
    public const int BlobFlag = 0x04;

    /// <summary>Index flag allowing duplicate key values.</summary>
    public const int IndexDuplicateFlag = 0x01;
    /// <summary>Index flag indicating an optional key.</summary>
    public const int IndexOptionalFlag = 0x02;
    /// <summary>Index flag indicating case-insensitive comparison.</summary>
    public const int IndexNoCaseFlag = 0x04;
    /// <summary>Index flag identifying the primary key.</summary>
    public const int IndexPrimaryFlag = 0x10;
    /// <summary>Index-component flag indicating descending order.</summary>
    public const int IndexComponentDescendingFlag = 0x01;
}
