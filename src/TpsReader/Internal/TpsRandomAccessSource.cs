namespace TpsReader.Internal;

internal interface ITpsRandomAccessSource : IDisposable
{
    int Length { get; }

    void ReadExactly(int offset, Span<byte> destination);
}

internal static class TpsRandomAccessSource
{
    public static ITpsRandomAccessSource OpenPath(string path) => new FileSource(path);

    public static ITpsRandomAccessSource OpenStream(Stream stream)
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

        return new StreamSource(stream, leaveOpen: true);
    }

    public static ITpsRandomAccessSource OpenBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new ByteArraySource(data);
    }

    private sealed class FileSource : ITpsRandomAccessSource
    {
        private readonly FileStream _stream;

        public FileSource(string path)
        {
            _stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.RandomAccess);
            Length = ValidateLength(_stream.Length);
        }

        public int Length { get; }

        public void ReadExactly(int offset, Span<byte> destination)
        {
            ValidateRange(offset, destination.Length, Length);
            var totalRead = 0;
            while (totalRead < destination.Length)
            {
                var bytesRead = RandomAccess.Read(
                    _stream.SafeFileHandle,
                    destination[totalRead..],
                    offset + totalRead);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException(
                        $"Reached the end of the TPS source after reading {totalRead} of {destination.Length} bytes.");
                }

                totalRead += bytesRead;
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed class StreamSource : ITpsRandomAccessSource
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly long _origin;
        private readonly object _gate = new();

        public StreamSource(Stream stream, bool leaveOpen)
        {
            _stream = stream;
            _leaveOpen = leaveOpen;
            _origin = stream.Position;
            Length = ValidateLength(Math.Max(0, stream.Length - _origin));
        }

        public int Length { get; }

        public void ReadExactly(int offset, Span<byte> destination)
        {
            ValidateRange(offset, destination.Length, Length);
            lock (_gate)
            {
                _stream.Position = checked(_origin + offset);
                _stream.ReadExactly(destination);
            }
        }

        public void Dispose()
        {
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private sealed class ByteArraySource(byte[] data) : ITpsRandomAccessSource
    {
        public int Length => data.Length;

        public void ReadExactly(int offset, Span<byte> destination)
        {
            ValidateRange(offset, destination.Length, data.Length);
            data.AsSpan(offset, destination.Length).CopyTo(destination);
        }

        public void Dispose()
        {
        }
    }

    private static int ValidateLength(long length)
    {
        if (length > int.MaxValue)
        {
            throw new NotSupportedException($"TPS inputs larger than {int.MaxValue} bytes are not supported.");
        }

        return checked((int)length);
    }

    private static void ValidateRange(int offset, int length, int sourceLength)
    {
        if (offset < 0 || length < 0 || offset > sourceLength - length)
        {
            throw new InvalidDataException(
                $"Cannot read {length} byte(s) at offset {offset}; source length is {sourceLength}.");
        }
    }
}

internal sealed class TpsRandomAccessReader
{
    private const int EncryptionBlockSize = 64;
    private const int HeaderSize = 0x200;
    private readonly ITpsRandomAccessSource _source;
    private readonly TpsEncryptionKey? _encryptionKey;

    public TpsRandomAccessReader(ITpsRandomAccessSource source, string? owner)
    {
        _source = source;
        if (source.Length < HeaderSize)
        {
            throw new InvalidDataException("TPS file is smaller than its header.");
        }

        var headerBytes = ReadRaw(0, HeaderSize);
        try
        {
            Header = new TpsHeader(new TpsBinaryReader(headerBytes));
        }
        catch (InvalidDataException) when (!string.IsNullOrEmpty(owner))
        {
            _encryptionKey = new TpsEncryptionKey(owner);
            _encryptionKey.Decrypt(headerBytes, 0, headerBytes.Length);
            Header = new TpsHeader(new TpsBinaryReader(headerBytes));
            IsEncrypted = true;
        }
    }

    public int Length => _source.Length;
    public bool IsEncrypted { get; }
    public TpsHeader Header { get; }

    public byte[] ReadBytes(int offset, int length)
    {
        if (!IsEncrypted)
        {
            return ReadRaw(offset, length);
        }

        var encryptedRange = FindEncryptedRange(offset, length);
        var alignedStart = offset & ~(EncryptionBlockSize - 1);
        var requestedEnd = checked(offset + length);
        var alignedEnd = checked((requestedEnd + EncryptionBlockSize - 1) & ~(EncryptionBlockSize - 1));
        if (alignedStart < encryptedRange.Start || alignedEnd > encryptedRange.End)
        {
            throw new InvalidDataException(
                $"Encrypted TPS read {offset}..{requestedEnd} crosses its encrypted block boundary.");
        }

        var encrypted = ReadRaw(alignedStart, alignedEnd - alignedStart);
        _encryptionKey!.Decrypt(encrypted, 0, encrypted.Length);
        var result = new byte[length];
        encrypted.AsSpan(offset - alignedStart, length).CopyTo(result);
        return result;
    }

    private (int Start, int End) FindEncryptedRange(int offset, int length)
    {
        var end = checked(offset + length);
        if (offset >= 0 && end <= HeaderSize)
        {
            return (0, HeaderSize);
        }

        for (var i = 0; i < Header.PageStarts.Count; i++)
        {
            var start = Header.PageStarts[i];
            var blockEnd = Header.PageEnds[i];
            if (offset >= start && end <= blockEnd)
            {
                return (start, blockEnd);
            }
        }

        throw new InvalidDataException($"TPS read {offset}..{end} is outside an encrypted block.");
    }

    private byte[] ReadRaw(int offset, int length)
    {
        var result = new byte[length];
        _source.ReadExactly(offset, result);
        return result;
    }
}
