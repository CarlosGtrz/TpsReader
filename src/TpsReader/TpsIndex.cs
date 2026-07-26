namespace TpsReader;

/// <summary>Describes an index declared by a TPS table.</summary>
public sealed class TpsIndex
{
    internal TpsIndex(
        int indexNumber,
        string name,
        int fieldsInKey,
        int flags = 0,
        string? externalName = null,
        IReadOnlyList<TpsIndexComponent>? components = null)
    {
        IndexNumber = indexNumber;
        Name = name;
        FieldsInKey = fieldsInKey;
        Flags = flags;
        ExternalName = externalName ?? string.Empty;
        Components = components ?? [];
    }

    /// <summary>Gets the one-based index ordinal in the table schema.</summary>
    public int IndexNumber { get; }

    /// <summary>Gets the schema name of the index.</summary>
    public string Name { get; }

    /// <summary>Gets the number of fields in the index key.</summary>
    public int FieldsInKey { get; }

    /// <summary>Gets the raw TPS index flags.</summary>
    public int Flags { get; }

    /// <summary>Gets the external file name stored in TPS metadata.</summary>
    public string ExternalName { get; }

    /// <summary>Gets the ordered field components of the index.</summary>
    public IReadOnlyList<TpsIndexComponent> Components { get; }
}
