using System.Text;

namespace TpsReader;

/// <summary>Controls how a TPS input is decoded and how damaged pages are handled.</summary>
public sealed class TpsOpenOptions
{
    /// <summary>The default bounded read-ahead window used by path and stream sources.</summary>
    public const int DefaultReadAheadBufferBytes = 64 * 1024;

    /// <summary>Gets the owner/password used to open an encrypted TPS input.</summary>
    public string? Owner { get; init; }

    /// <summary>Gets whether unreadable pages should be skipped for partial recovery.</summary>
    public bool IgnoreErrors { get; init; }

    /// <summary>Gets the encoding used for schema names, strings, GROUP projections, and MEMOs.</summary>
    public Encoding StringEncoding { get; init; } = Encoding.Latin1;

    /// <summary>Gets the optional receiver for byte-based open progress.</summary>
    public IProgress<TpsReadProgress>? Progress { get; init; }

    /// <summary>
    /// Gets the maximum read-ahead window for path and seekable-stream inputs.
    /// Set to zero to disable managed read-ahead buffering.
    /// </summary>
    public int ReadAheadBufferBytes { get; init; } = DefaultReadAheadBufferBytes;

}
