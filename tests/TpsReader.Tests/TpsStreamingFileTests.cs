using System.Buffers.Binary;
using System.Text;
using TpsReader.Internal;

namespace TpsReader.Tests;

public sealed class TpsStreamingFileTests
{
    [Fact]
    public void Path_streaming_exposes_schema_records_and_cached_count()
    {
        var progress = new RecordingProgress();
        using var streamed = TpsFile.OpenStreaming(
            Fixture("CUSTOMER.TPS"),
            new TpsOpenOptions { Progress = progress });
        var materialized = TpsFile.Open(Fixture("CUSTOMER.TPS"));

        var table = streamed.GetTable();
        Assert.Empty(table.Records);
        var records = streamed.ReadRecords(table).ToArray();

        Assert.Equal(materialized.GetTable().Records.Count, records.Length);
        Assert.Equal(
            materialized.GetTable().Records.Select(record => record.GetInt32("CUSTNUMBER")),
            records.Select(record => record.GetInt32("CUSTNUMBER")));
        Assert.Contains(progress.Events, item => item.Stage == TpsReadStage.StreamingRecords);

        var countEvents = progress.Events.Count(item => item.Stage == TpsReadStage.CountingRecords);
        Assert.Equal(records.LongLength, streamed.CountRecords(table));
        Assert.Equal(records.LongLength, streamed.CountRecords(table));
        Assert.Equal(countEvents, progress.Events.Count(item => item.Stage == TpsReadStage.CountingRecords));
    }

    [Fact]
    public void Count_scans_when_records_have_not_already_been_fully_enumerated()
    {
        var progress = new RecordingProgress();
        using var streamed = TpsFile.OpenStreaming(
            Fixture("CUSTOMER.TPS"),
            new TpsOpenOptions { Progress = progress });

        Assert.Equal(7, streamed.CountRecords(streamed.GetTable()));
        Assert.Contains(progress.Events, item => item.Stage == TpsReadStage.CountingRecords);
    }

    [Fact]
    public void Seekable_stream_uses_its_current_position_and_remains_open()
    {
        var contents = File.ReadAllBytes(Fixture("CUSTOMER.TPS"));
        var prefixed = new byte[contents.Length + 23];
        contents.CopyTo(prefixed, 23);
        using var input = new MemoryStream(prefixed) { Position = 23 };

        using (var streamed = TpsFile.OpenStreaming(input))
        {
            Assert.Equal(7, streamed.ReadRecords(streamed.GetTable()).Count());
        }

        Assert.True(input.CanRead);
    }

    [Fact]
    public void Non_seekable_stream_is_rejected_without_being_consumed()
    {
        using var input = new TrackingNonSeekableStream(File.ReadAllBytes(Fixture("CUSTOMER.TPS")));

        var error = Assert.Throws<NotSupportedException>(() => TpsFile.OpenStreaming(input));

        Assert.Contains("seekable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, input.BytesRead);
    }

    [Fact]
    public void Try_open_streaming_returns_structured_metadata_errors()
    {
        var bytes = "not a topspeed file"u8.ToArray();

        var opened = TpsFile.TryOpenStreaming(bytes, out var file, out var error);

        Assert.False(opened);
        Assert.Null(file);
        Assert.NotNull(error);
        Assert.Contains("TPS byte array", error.Message);
    }

    [Fact]
    public void Encrypted_streaming_matches_materialized_and_does_not_modify_input()
    {
        var bytes = File.ReadAllBytes(Fixture("encrypted-a.tps"));
        var original = bytes.ToArray();
        var options = new TpsOpenOptions { Owner = "a" };
        using var streamed = TpsFile.OpenStreaming(bytes, options);
        var materialized = TpsFile.Open(bytes, options);

        Assert.True(streamed.IsEncrypted);
        Assert.Equal(
            materialized.GetTable().Records.Select(record => record.GetInt32("WERKNMR")),
            streamed.ReadRecords(streamed.GetTable()).Select(record => record.GetInt32("WERKNMR")));
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void Active_operations_are_exclusive_and_returned_records_outlive_the_reader()
    {
        var streamed = TpsFile.OpenStreaming(Fixture("CUSTOMER.TPS"));
        var table = streamed.GetTable();
        using var enumerator = streamed.ReadRecords(table).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var first = enumerator.Current;

        Assert.Throws<InvalidOperationException>(() => streamed.CountRecords(table));
        enumerator.Dispose();
        streamed.Dispose();

        Assert.NotNull(first.GetString("COMPANY"));
        Assert.Throws<ObjectDisposedException>(() => streamed.CountRecords(table));
    }

    [Fact]
    public void Cancelled_count_is_not_cached()
    {
        using var streamed = TpsFile.OpenStreaming(Fixture("CUSTOMER.TPS"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            streamed.CountRecords(streamed.GetTable(), cancellation.Token));
        Assert.Equal(7, streamed.CountRecords(streamed.GetTable()));
    }

    [Fact]
    public void Large_logical_source_uses_only_the_bounded_read_ahead_window()
    {
        var source = new TrackingLargeSource(
            File.ReadAllBytes(Fixture("CUSTOMER.TPS")),
            220 * 1024 * 1024);
        using var streamed = TpsStreamingFile.Create(
            source,
            new TpsOpenOptions(),
            "stream",
            sourcePath: null);

        Assert.Equal(7, streamed.ReadRecords(streamed.GetTable()).Count());
        Assert.InRange(
            source.MaximumReadLength,
            1,
            TpsOpenOptions.DefaultReadAheadBufferBytes);
        Assert.True(source.TotalBytesRead <= TpsOpenOptions.DefaultReadAheadBufferBytes);
    }

    [Fact]
    public void Read_ahead_substantially_reduces_underlying_source_reads()
    {
        var contents = File.ReadAllBytes(Fixture("CUSTOMER.TPS"));
        var unbufferedSource = new TrackingLargeSource(contents, 220 * 1024 * 1024);
        using (var unbuffered = TpsStreamingFile.Create(
                   unbufferedSource,
                   new TpsOpenOptions { ReadAheadBufferBytes = 0 },
                   "stream",
                   sourcePath: null))
        {
            Assert.Equal(7, unbuffered.ReadRecords(unbuffered.GetTable()).Count());
        }

        var bufferedSource = new TrackingLargeSource(contents, 220 * 1024 * 1024);
        using (var buffered = TpsStreamingFile.Create(
                   bufferedSource,
                   new TpsOpenOptions(),
                   "stream",
                   sourcePath: null))
        {
            Assert.Equal(7, buffered.ReadRecords(buffered.GetTable()).Count());
        }

        Assert.True(unbufferedSource.ReadCalls > bufferedSource.ReadCalls * 5);
        Assert.Equal(1, bufferedSource.ReadCalls);
    }

    [Fact]
    public void Negative_read_ahead_budget_is_rejected()
    {
        var bytes = File.ReadAllBytes(Fixture("CUSTOMER.TPS"));

        Assert.Throws<ArgumentOutOfRangeException>(() => TpsFile.OpenStreaming(
            bytes,
            new TpsOpenOptions { ReadAheadBufferBytes = -1 }));
    }

    [Fact]
    public void Memo_fragments_are_indexed_by_location_and_reassembled_in_sequence_order()
    {
        using var source = CreateMemoSource((1, "B"u8.ToArray()), (0, "A"u8.ToArray()));
        var reader = new TpsFileReader(source, null, Encoding.Latin1);

        var index = reader.BuildMemoIndex(14, ignoreErrors: false);
        var memo = Assert.IsType<MemoRecord>(reader.ReadMemo(index, 0, 17, ignoreErrors: false));

        Assert.Equal("AB", memo.ReadText(Encoding.Latin1));
        Assert.Equal(TpsMemoState.Complete, memo.FragmentState);
    }

    [Fact]
    public void Missing_memo_fragment_is_strict_or_recoverable()
    {
        using var source = CreateMemoSource((1, "tail"u8.ToArray()));
        var reader = new TpsFileReader(source, null, Encoding.Latin1);
        var index = reader.BuildMemoIndex(14, ignoreErrors: false);

        Assert.Throws<InvalidDataException>(() => reader.ReadMemo(index, 0, 17, ignoreErrors: false));
        var recovered = Assert.IsType<MemoRecord>(reader.ReadMemo(index, 0, 17, ignoreErrors: true));
        Assert.Equal("tail", recovered.ReadText(Encoding.Latin1));
        Assert.Equal(TpsMemoState.Damaged, recovered.FragmentState);
    }

    [Fact]
    public void Blob_fragments_are_reassembled_before_declared_length_validation()
    {
        using var source = CreateMemoSource(
            (0, [3, 0, 0, 0, 0x10]),
            (1, [0x20, 0x30]));
        var reader = new TpsFileReader(source, null, Encoding.Latin1);
        var index = reader.BuildMemoIndex(14, ignoreErrors: false);
        var blob = Assert.IsType<MemoRecord>(reader.ReadMemo(index, 0, 17, ignoreErrors: false));

        Assert.Equal([0x10, 0x20, 0x30], blob.ReadBlob(ignoreErrors: false));
    }

    private static ITpsRandomAccessSource CreateMemoSource(params (int Sequence, byte[] Payload)[] fragments)
    {
        var source = new byte[0x900];
        File.ReadAllBytes(Fixture("CUSTOMER.TPS")).AsSpan(0, 0x200).CopyTo(source);
        var records = new List<byte>();
        foreach (var fragment in fragments)
        {
            var data = new byte[12 + fragment.Payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(data, 14);
            data[4] = 0xFC;
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(5), 17);
            data[9] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10), checked((ushort)fragment.Sequence));
            fragment.Payload.CopyTo(data, 12);

            records.Add(0xC0);
            records.AddRange(BitConverter.GetBytes(checked((ushort)data.Length)));
            records.AddRange(BitConverter.GetBytes((ushort)12));
            records.AddRange(data);
        }

        var pageSize = checked((ushort)(13 + records.Count));
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(0x200), 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(0x204), pageSize);
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(0x206), pageSize);
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(0x208), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(0x20A), checked((ushort)fragments.Length));
        source[0x20C] = 0;
        records.CopyTo(source, 0x20D);
        return TpsRandomAccessSource.OpenBytes(source);
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class RecordingProgress : IProgress<TpsReadProgress>
    {
        public List<TpsReadProgress> Events { get; } = [];
        public void Report(TpsReadProgress value) => Events.Add(value);
    }

    private sealed class TrackingLargeSource(byte[] contents, int length) : ITpsRandomAccessSource
    {
        public int MaximumReadLength { get; private set; }
        public long TotalBytesRead { get; private set; }
        public int ReadCalls { get; private set; }
        public int Length => length;

        public void ReadExactly(int offset, Span<byte> destination)
        {
            ReadCalls++;
            MaximumReadLength = Math.Max(MaximumReadLength, destination.Length);
            TotalBytesRead += destination.Length;
            destination.Clear();
            if (offset < contents.Length)
            {
                contents.AsSpan(offset, Math.Min(destination.Length, contents.Length - offset))
                    .CopyTo(destination);
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class TrackingNonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
