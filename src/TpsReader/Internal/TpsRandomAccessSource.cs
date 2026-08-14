namespace TpsReader.Internal;

internal interface ITpsRandomAccessSource : IDisposable
{
    int Length { get; }

    void ReadExactly(int offset, Span<byte> destination);
}

internal interface IMemoryBackedTpsSource
{
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

    public static ITpsRandomAccessSource WithReadAhead(
        ITpsRandomAccessSource source,
        int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        return bufferSize == 0 || source is IMemoryBackedTpsSource
            ? source
            : new BufferedSource(source, bufferSize);
    }

    private sealed class BufferedSource(ITpsRandomAccessSource source, int bufferSize) : ITpsRandomAccessSource
    {
        private readonly byte[] _buffer = new byte[Math.Min(bufferSize, source.Length)];
        private int _bufferStart = -1;
        private int _bufferLength;

        public int Length => source.Length;

        public void ReadExactly(int offset, Span<byte> destination)
        {
            ValidateRange(offset, destination.Length, Length);
            if (_buffer.Length == 0 || destination.Length > _buffer.Length)
            {
                source.ReadExactly(offset, destination);
                return;
            }

            var copied = 0;
            while (copied < destination.Length)
            {
                var currentOffset = offset + copied;
                if (currentOffset < _bufferStart || currentOffset >= _bufferStart + _bufferLength)
                {
                    Fill(currentOffset);
                }

                var available = Math.Min(
                    destination.Length - copied,
                    _bufferStart + _bufferLength - currentOffset);
                _buffer.AsSpan(currentOffset - _bufferStart, available)
                    .CopyTo(destination[copied..]);
                copied += available;
            }
        }

        public void Dispose()
        {
            // The owner disposes the wrapped source; this window only owns managed memory.
        }

        private void Fill(int requestedOffset)
        {
            _bufferStart = requestedOffset / _buffer.Length * _buffer.Length;
            _bufferLength = Math.Min(_buffer.Length, Length - _bufferStart);
            source.ReadExactly(_bufferStart, _buffer.AsSpan(0, _bufferLength));
        }
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

    private sealed class ByteArraySource(byte[] data) : ITpsRandomAccessSource, IMemoryBackedTpsSource
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
