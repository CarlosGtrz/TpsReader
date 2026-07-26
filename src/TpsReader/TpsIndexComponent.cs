namespace TpsReader;

/// <summary>Describes one ordered field component of a TPS index.</summary>
public sealed class TpsIndexComponent
{
    internal TpsIndexComponent(int rank, int fieldIndex, int flags)
    {
        Rank = rank;
        FieldIndex = fieldIndex;
        Flags = flags;
    }

    /// <summary>Gets the one-based component position in the key.</summary>
    public int Rank { get; }

    /// <summary>Gets the zero-based field index stored in TPS metadata.</summary>
    public int FieldIndex { get; }

    /// <summary>Gets the raw TPS component flags.</summary>
    public int Flags { get; }

    /// <summary>Gets whether this key component is sorted in ascending order.</summary>
    public bool IsAscending => (Flags & TpsFormatConstants.IndexComponentDescendingFlag) == 0;
}
