using System.Text;
using TpsReader.Internal;

namespace TpsReader.Tests;

public sealed class PortableImprovementsTests
{
    [Fact]
    public void False_nested_page_candidate_does_not_hide_the_containing_page()
    {
        var bytes = new byte[0x300];
        WriteInt32(bytes, 0, 0);
        WriteUInt16(bytes, 4, 0x20D);
        WriteUInt16(bytes, 6, 0x20D);
        bytes[12] = 1;

        WriteInt32(bytes, 0x100, 0x100);
        WriteUInt16(bytes, 0x104, 0);

        var block = new TpsBlock(new TpsBinaryReader(bytes), 0, bytes.Length, ignoreErrors: false);

        var page = Assert.Single(block.Pages);
        Assert.Equal(0, page.SourceOffset);
    }

    [Fact]
    public void Metadata_only_open_exposes_schema_without_scanning_content()
    {
        var progress = new RecordingProgress();

        var file = TpsFile.OpenMetadata(
            Fixture("CUSTOMER.TPS"),
            new TpsOpenOptions { Progress = progress });

        Assert.True(file.IsMetadataOnly);
        Assert.False(file.IsEncrypted);
        Assert.Equal(0, file.RecoveryIssueCount);
        var table = Assert.Single(file.Tables);
        Assert.Equal(110, table.RecordLength);
        Assert.Empty(table.Records);

        Assert.Contains(progress.Events, item => item.Stage == TpsReadStage.LoadingSource);
        Assert.Contains(progress.Events, item => item.Stage == TpsReadStage.ScanningDefinitions);
        Assert.DoesNotContain(progress.Events, item => item.Stage == TpsReadStage.ScanningRecordsAndMemos);
        Assert.DoesNotContain(progress.Events, item => item.Stage == TpsReadStage.DecryptingSource);
        AssertProgressIsBoundedAndMonotonic(progress.Events);
    }

    [Fact]
    public void Metadata_only_stream_and_byte_array_overloads_leave_inputs_usable()
    {
        var bytes = File.ReadAllBytes(Fixture("CUSTOMER.TPS"));
        using var stream = new MemoryStream(bytes);

        var fromStream = TpsFile.OpenMetadata(stream);
        var fromBytes = TpsFile.OpenMetadata(bytes);

        Assert.True(stream.CanRead);
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(fromStream.IsMetadataOnly);
        Assert.True(fromBytes.IsMetadataOnly);
        Assert.Empty(fromStream.GetTable().Records);
        Assert.Empty(fromBytes.GetTable().Records);
    }

    [Fact]
    public void Full_open_reports_content_progress_and_source_offsets()
    {
        var progress = new RecordingProgress();

        var file = TpsFile.Open(
            Fixture("CUSTOMER.TPS"),
            new TpsOpenOptions { Progress = progress });

        Assert.False(file.IsMetadataOnly);
        Assert.Contains(progress.Events, item => item.Stage == TpsReadStage.ScanningRecordsAndMemos);
        AssertProgressIsBoundedAndMonotonic(progress.Events);

        var records = file.GetTable().Records;
        Assert.NotEmpty(records);
        Assert.All(records, record => Assert.True(record.SourcePageOffset >= 0x200));
        Assert.True(records.Select(record => record.SourcePageOffset).SequenceEqual(
            records.Select(record => record.SourcePageOffset).Order()));
    }

    [Fact]
    public void Encryption_state_distinguishes_decrypted_and_plaintext_owner_calls()
    {
        var encryptedProgress = new RecordingProgress();
        var encrypted = TpsFile.OpenMetadata(
            Fixture("encrypted-a.tps"),
            new TpsOpenOptions
            {
                Owner = "a",
                Progress = encryptedProgress
            });
        var plaintext = TpsFile.OpenMetadata(
            Fixture("CUSTOMER.TPS"),
            new TpsOpenOptions { Owner = "unused-owner" });

        Assert.True(encrypted.IsEncrypted);
        Assert.Contains(encryptedProgress.Events, item => item.Stage == TpsReadStage.DecryptingSource);
        Assert.False(plaintext.IsEncrypted);
    }

    [Fact]
    public void Existing_fixture_exposes_raw_field_and_complete_index_metadata()
    {
        var table = TpsFile.OpenMetadata(Fixture("CUSTOMER.TPS")).GetTable();
        var company = table.Fields.Single(field => field.ShortName == "COMPANY");
        var firstIndex = table.Indexes[0];
        var component = Assert.Single(firstIndex.Components);

        Assert.Equal(TpsFormatConstants.FieldString, company.RawTypeCode);
        Assert.Equal(1, company.IndexNumber);
        Assert.Equal(20, company.StringLength);
        Assert.Equal(string.Empty, company.StringMask);

        Assert.Equal(6, firstIndex.Flags);
        Assert.Equal(string.Empty, firstIndex.ExternalName);
        Assert.Equal(firstIndex.FieldsInKey, firstIndex.Components.Count);
        Assert.Equal(1, component.Rank);
        Assert.Equal(0, component.FieldIndex);
        Assert.True(component.IsAscending);
    }

    [Fact]
    public void Recovery_count_tracks_each_discarded_page_pass()
    {
        var damaged = CreateTwoPageCustomerWithDamagedSecondPage();

        Assert.Throws<TpsParseException>(() => TpsFile.Open(damaged));

        var recovered = TpsFile.Open(
            damaged,
            new TpsOpenOptions { IgnoreErrors = true });

        Assert.Equal(2, recovered.RecoveryIssueCount);
        Assert.Equal(7, recovered.GetTable().Records.Count);
    }

    [Fact]
    public void Memo_state_distinguishes_empty_complete_and_damaged_values()
    {
        var memo = new TpsMemo(1, "T:NOTE", "NOTE", 0, isBlob: false);
        var blob = new TpsMemo(2, "T:DATA", "DATA", TpsFormatConstants.BlobFlag, isBlob: true);
        var record = new TpsRecord(
            1,
            new Dictionary<string, object?>(),
            new Dictionary<string, TpsMemoValue>(StringComparer.OrdinalIgnoreCase)
            {
                [memo.Name] = new TpsMemoValue(memo, null, null, TpsMemoState.Empty),
                [blob.Name] = new TpsMemoValue(blob, null, [1, 2], TpsMemoState.Damaged)
            });

        Assert.Equal(TpsMemoState.Empty, record.GetMemoState("T:NOTE"));
        Assert.Equal(TpsMemoState.Damaged, record.GetMemoState("T:DATA"));
        Assert.Equal([1, 2], record.GetBlob("T:DATA"));

        var complete = new TpsRecord(
            2,
            new Dictionary<string, object?>(),
            new Dictionary<string, TpsMemoValue>
            {
                [memo.Name] = new TpsMemoValue(memo, "text", null)
            });
        Assert.Equal(TpsMemoState.Complete, complete.GetMemoState("T:NOTE"));
    }

    private static byte[] CreateTwoPageCustomerWithDamagedSecondPage()
    {
        var source = File.ReadAllBytes(Fixture("CUSTOMER.TPS"));
        const int firstPageOffset = 0x200;
        var firstPageSize = BitConverter.ToUInt16(source, firstPageOffset + 4);
        const int secondPageOffset = 0x900;
        const int outputLength = 0x1000;
        var result = new byte[outputLength];

        Array.Copy(source, 0, result, 0, firstPageOffset);
        Array.Copy(source, firstPageOffset, result, firstPageOffset, firstPageSize);
        Array.Copy(source, firstPageOffset, result, secondPageOffset, firstPageSize);

        WriteInt32(result, secondPageOffset, secondPageOffset);
        WriteUInt16(result, secondPageOffset + 6, 13);

        var originalEndReference = (source.Length - firstPageOffset) / 0x100;
        var blockIndex = Enumerable.Range(0, (0x200 - 0x110) / 4)
            .Single(index =>
                BitConverter.ToInt32(source, 0x20 + index * 4) < originalEndReference &&
                BitConverter.ToInt32(source, 0x110 + index * 4) == originalEndReference);
        WriteInt32(
            result,
            0x110 + blockIndex * 4,
            (outputLength - firstPageOffset) / 0x100);
        return result;
    }

    private static void AssertProgressIsBoundedAndMonotonic(IReadOnlyList<TpsReadProgress> events)
    {
        Assert.All(events, item =>
        {
            Assert.True(item.BytesTotal >= 1);
            Assert.InRange(item.BytesCompleted, 0, item.BytesTotal);
        });

        foreach (var stage in events.GroupBy(item => item.Stage))
        {
            Assert.True(stage.Select(item => item.BytesCompleted).SequenceEqual(
                stage.Select(item => item.BytesCompleted).Order()));
        }
    }

    private static void WriteInt32(byte[] data, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(data, offset);

    private static void WriteUInt16(byte[] data, int offset, int value) =>
        BitConverter.GetBytes((ushort)value).CopyTo(data, offset);

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class RecordingProgress : IProgress<TpsReadProgress>
    {
        public List<TpsReadProgress> Events { get; } = [];

        public void Report(TpsReadProgress value) => Events.Add(value);
    }
}
