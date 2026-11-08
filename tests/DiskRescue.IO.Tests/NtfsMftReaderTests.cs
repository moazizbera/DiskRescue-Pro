using System.Buffers.Binary;
using System.Text;
using DiskRescue.Filesystems;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsMftReaderTests
{
    [Fact]
    public void OpensMftFromRecordZeroAndReadsAnotherRecordThroughValidatedRunlist()
    {
        using var image = CreateImage();
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());

        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));
        Assert.NotNull(opened.Reader);
        Assert.Equal((ulong)4096, opened.Reader.InitializedSize);

        var result = opened.Reader.ReadRecord(1);
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.NotNull(result.Record);
        Assert.Equal((uint)1, result.Record.RecordNumber);
        Assert.Equal((ushort)9, result.Record.SequenceNumber);
    }

    [Fact]
    public void DeletedFileNameMetadataIsOptInAndExplicitlyMarked()
    {
        using var image = CreateImage(deletedRecordOne: true);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        var normalScan = opened.Reader!.ScanRecords(16);
        Assert.True(normalScan.IsValid, string.Join("; ", normalScan.Issues));
        Assert.DoesNotContain(normalScan.Entries, entry => entry.RecordNumber == 1);

        var forensicScan = opened.Reader.ScanRecords(16, includeDeleted: true);
        Assert.True(forensicScan.IsValid, string.Join("; ", forensicScan.Issues));
        var deleted = Assert.Single(forensicScan.Entries, entry => entry.RecordNumber == 1);
        Assert.True(deleted.IsDeleted);
        Assert.Equal("Deleted.txt", deleted.Name);
        Assert.Equal((ulong)5, deleted.ParentRecordNumber);
        Assert.Equal((ushort)9, deleted.ParentSequenceNumber);
    }

    [Fact]
    public void ReadsReferencedRecordOnlyWhenSequenceAndBaseReferenceMatch()
    {
        const ulong baseReference = (9UL << 48);
        using var image = CreateImage(recordTwoBaseReferenceRaw: baseReference);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        var result = opened.Reader!.ReadReferencedRecord(
            new NtfsFileReference(2, 9), new NtfsFileReference(0, 9));

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal((uint)2, result.Record!.RecordNumber);
    }

    [Fact]
    public void RejectsReferencedRecordWithStaleSequenceNumber()
    {
        using var image = CreateImage(recordTwoBaseReferenceRaw: (9UL << 48));
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        var result = opened.Reader!.ReadReferencedRecord(
            new NtfsFileReference(2, 8), new NtfsFileReference(0, 9));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("sequence number", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsReferencedRecordWithWrongBaseFileReference()
    {
        using var image = CreateImage(recordTwoBaseReferenceRaw: (9UL << 48) | 1UL);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        var result = opened.Reader!.ReadReferencedRecord(
            new NtfsFileReference(2, 9), new NtfsFileReference(0, 9));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("base-file reference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsRecordOutsideInitializedMftData()
    {
        using var image = CreateImage();
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        var result = opened.Reader!.ReadRecord(4);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("initialized", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiscoversResidentAttributeListContinuationAndReadsBeyondInitialMftExtent()
    {
        using var image = CreateImage(recordTwoBaseReferenceRaw: (9UL << 48), includeAttributeList: true, includeContinuation: true);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        var extended = opened.Reader!.ExtendUsingResidentAttributeList();

        Assert.True(extended.IsValid, string.Join("; ", extended.Issues));
        Assert.True(extended.Extended);
        var record = extended.Reader!.ReadRecord(4);
        Assert.True(record.IsValid, string.Join("; ", record.Issues));
        Assert.Equal((uint)4, record.Record!.RecordNumber);

        var scan = extended.Reader.ScanRecords(16);
        Assert.True(scan.IsValid, string.Join("; ", scan.Issues));
        Assert.Equal((ulong)8, scan.RecordsExamined);
    }

    [Fact]
    public void CopiesResidentFileDataToCallerOwnedStreamWithoutChangingSource()
    {
        var expected = Encoding.UTF8.GetBytes("DiskRescue recovered content");
        using var image = CreateImage(residentFileData: expected);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        var stream = opened.Reader!.OpenDataStream(1);
        Assert.True(stream.IsValid, string.Join("; ", stream.Issues));
        Assert.True(stream.IsResident);
        Assert.Equal((ulong)expected.Length, stream.Length);

        using var destination = new MemoryStream();
        var copied = stream.Reader!.CopyTo(destination, 1024);

        Assert.True(copied.IsValid, string.Join("; ", copied.Issues));
        Assert.Equal((ulong)expected.Length, copied.BytesWritten);
        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public void CopiesSingleExtentNonResidentDataAndZeroFillsUninitializedTail()
    {
        var initialized = Enumerable.Range(0, 512).Select(value => (byte)(value % 251)).ToArray();
        var imageData = new byte[600];
        initialized.CopyTo(imageData, 0);
        using var image = CreateImage(nonResidentFileData: imageData);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        var stream = opened.Reader!.OpenDataStream(1);
        Assert.True(stream.IsValid, string.Join("; ", stream.Issues));
        Assert.False(stream.IsResident);
        Assert.Equal((ulong)600, stream.Length);

        using var destination = new MemoryStream();
        var copied = stream.Reader!.CopyTo(destination, 1024);

        Assert.True(copied.IsValid, string.Join("; ", copied.Issues));
        Assert.Equal((ulong)600, copied.BytesWritten);
        Assert.Equal(initialized, destination.ToArray()[..512]);
        Assert.All(destination.ToArray()[512..], value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void CopiesValidatedMultiExtentFileDataAcrossAttributeListContinuation()
    {
        var first = Enumerable.Range(0, 512).Select(value => (byte)(value % 239)).ToArray();
        var second = Enumerable.Range(0, 512).Select(value => (byte)((value + 71) % 241)).ToArray();
        using var image = CreateImage(includeMultiExtentFileData: true,
            firstFileDataExtent: first, secondFileDataExtent: second);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        var stream = opened.Reader!.OpenDataStream(1);
        Assert.True(stream.IsValid, string.Join("; ", stream.Issues));
        Assert.False(stream.IsResident);
        Assert.Equal((ulong)1024, stream.Length);

        using var destination = new MemoryStream();
        var copied = stream.Reader!.CopyTo(destination, 2048);

        Assert.True(copied.IsValid, string.Join("; ", copied.Issues));
        Assert.Equal((ulong)1024, copied.BytesWritten);
        Assert.Equal(first.Concat(second).ToArray(), destination.ToArray());
    }

    [Fact]
    public void RefusesFileDataExportAboveCallerSpecifiedLimitBeforeWriting()
    {
        using var image = CreateImage(residentFileData: Encoding.UTF8.GetBytes("bounded export"));
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));
        var stream = opened.Reader!.OpenDataStream(1);
        Assert.True(stream.IsValid, string.Join("; ", stream.Issues));

        using var destination = new MemoryStream();
        var copied = stream.Reader!.CopyTo(destination, 2);

        Assert.False(copied.IsValid);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void RejectsMftBaseRecordWithWrongRecordNumber()
    {
        using var image = CreateImage(recordZeroNumber: 99);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());

        Assert.False(opened.IsValid);
        Assert.Contains(opened.Issues, x => x.Contains("record 0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeletedStreamCopyRequiresPreflightAndCopiesOnlyBitmapFreeClusters()
    {
        var expected = Enumerable.Range(0, 512).Select(value => (byte)(value % 251)).ToArray();
        using var image = CreateDeletedFileImage(expected, allocatedDataClusters: false);
        var sourceBefore = new byte[512];
        image.ReadExactlyAt(30 * 512, sourceBefore);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        using var destination = new MemoryStream();
        var result = opened.Reader!.CopyDeletedDataStreamTo(1, destination, 1024);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        var sourceAfter = new byte[512];
        image.ReadExactlyAt(30 * 512, sourceAfter);
        Assert.Equal(sourceBefore, sourceAfter);
        Assert.NotNull(result.Assessment);
        Assert.Equal(NtfsDeletedDataStreamStatus.ReadyForBoundedRead, result.Assessment!.Status);
        Assert.NotNull(result.Copy);
        Assert.Equal((ulong)512, result.Copy!.BytesWritten);
        Assert.Equal(expected, destination.ToArray());
        Assert.DoesNotContain(result.Assessment.Warnings, warning =>
            warning.Contains("integrity verified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeletedResidentStreamCopiesFromValidatedMftRecord()
    {
        var expected = Enumerable.Range(0, 512).Select(value => (byte)(value % 233)).ToArray();
        using var image = CreateDeletedFileImage(expected, allocatedDataClusters: false, resident: true);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        using var destination = new MemoryStream();
        var result = opened.Reader!.CopyDeletedDataStreamTo(1, destination, 1024);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.True(result.Assessment!.IsResident);
        Assert.Equal((ulong)512, result.Copy!.BytesWritten);
        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public void DeletedStreamCopyHonorsCancellationBeforeAnyReadOrWrite()
    {
        var payload = Enumerable.Range(0, 512).Select(value => (byte)value).ToArray();
        using var image = CreateDeletedFileImage(payload, allocatedDataClusters: false);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            opened.Reader!.CopyDeletedDataStreamTo(1, destination, 1024,
                cancellationToken: cancellation.Token));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void DeletedStreamCopyRefusesClustersMarkedAllocated()
    {
        var payload = Enumerable.Range(0, 512).Select(value => (byte)value).ToArray();
        using var image = CreateDeletedFileImage(payload, allocatedDataClusters: true);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        using var destination = new MemoryStream();
        var result = opened.Reader!.CopyDeletedDataStreamTo(1, destination, 1024);

        Assert.False(result.IsValid);
        Assert.Equal(NtfsDeletedDataStreamStatus.ClustersAllocated, result.Assessment!.Status);
        Assert.Null(result.Copy);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void DeletedStreamCopyEnforcesMaximumBytesBeforeWriting()
    {
        var payload = Enumerable.Range(0, 512).Select(value => (byte)value).ToArray();
        using var image = CreateDeletedFileImage(payload, allocatedDataClusters: false);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        using var destination = new MemoryStream();
        var result = opened.Reader!.CopyDeletedDataStreamTo(1, destination, 128);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("export limit", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void DeletedStreamCopyRefusesRunlistBeyondSuppliedVolumeBoundary()
    {
        var payload = Enumerable.Range(0, 512).Select(value => (byte)value).ToArray();
        using var image = CreateDeletedFileImage(payload, allocatedDataClusters: false);
        const long truncatedVolumeLength = 31 * 512;
        var opened = NtfsMftReader.Open(image, 0, truncatedVolumeLength, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        using var destination = new MemoryStream();
        var result = opened.Reader!.CopyDeletedDataStreamTo(1, destination, 1024);

        Assert.False(result.IsValid);
        Assert.Equal(NtfsDeletedDataStreamStatus.AllocationUnknown, result.Assessment!.Status);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void DeletedStreamCopyRejectsActiveMftRecord()
    {
        var payload = Enumerable.Range(0, 512).Select(value => (byte)value).ToArray();
        using var image = CreateDeletedFileImage(payload, allocatedDataClusters: false, deleted: false);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        using var destination = new MemoryStream();
        var result = opened.Reader!.CopyDeletedDataStreamTo(1, destination, 1024);

        Assert.False(result.IsValid);
        Assert.Equal(NtfsDeletedDataStreamStatus.MetadataRejected, result.Assessment!.Status);
        Assert.Equal(0, destination.Length);
    }

    private static MemoryImage CreateDeletedFileImage(byte[] payload, bool allocatedDataClusters, bool deleted = true, bool resident = false)
    {
        if (payload.Length != 512)
            throw new ArgumentException("Synthetic deleted-file payload must be exactly one cluster.", nameof(payload));

        var bytes = new byte[128 * 512];
        var mftBase = CreateFileRecord(0, includeMftDataAttribute: true, mftSize: 8192);
        // Replace the initial $MFT map with one contiguous 16-cluster extent at LCN 4.
        BinaryPrimitives.WriteUInt64LittleEndian(mftBase.AsSpan(80, 8), 15);
        BinaryPrimitives.WriteUInt64LittleEndian(mftBase.AsSpan(96, 8), 8192);
        BinaryPrimitives.WriteUInt64LittleEndian(mftBase.AsSpan(104, 8), 8192);
        BinaryPrimitives.WriteUInt64LittleEndian(mftBase.AsSpan(112, 8), 8192);
        mftBase[120] = 0x11;
        mftBase[121] = 16;
        mftBase[122] = 4;
        mftBase[123] = 0;
        mftBase.AsSpan(124, 4).Clear();
        mftBase.AsSpan(0, 1024).CopyTo(bytes.AsSpan(4 * 512, 1024));

        var file = CreateFileRecord(1, includeMftDataAttribute: false, includeFileName: true,
            isInUse: !deleted, residentData: resident ? payload : null,
            nonResidentDataLength: resident ? 0 : 512);
        file.AsSpan(0, 1024).CopyTo(bytes.AsSpan(6 * 512, 1024));

        var parent = CreateFileRecord(5, includeMftDataAttribute: false, isDirectory: true);
        parent.AsSpan(0, 1024).CopyTo(bytes.AsSpan(14 * 512, 1024));

        var bitmap = new byte[16];
        if (allocatedDataClusters)
        {
            // LCNs 30 and 31 are represented by bits 6 and 7 in bitmap byte 3.
            bitmap[3] = 0b1100_0000;
        }
        var bitmapRecord = CreateFileRecord(6, includeMftDataAttribute: false, residentData: bitmap);
        bitmapRecord.AsSpan(0, 1024).CopyTo(bytes.AsSpan(16 * 512, 1024));

        if (!resident)
            payload.CopyTo(bytes.AsSpan(30 * 512, 512));
        return new MemoryImage(bytes);
    }

    private static NtfsBootSector CreateBootSector() =>
        new(512, 1, 512, 128, 4, 8, 1024, 1024, 123, 2048, 4096);

    private static MemoryImage CreateImage(uint recordZeroNumber = 0, ulong recordTwoBaseReferenceRaw = 0,
        bool includeAttributeList = false, bool includeContinuation = false, byte[]? residentFileData = null,
        byte[]? nonResidentFileData = null, bool includeMultiExtentFileData = false,
        byte[]? firstFileDataExtent = null, byte[]? secondFileDataExtent = null,
        bool deletedRecordOne = false)
    {
        var bytes = new byte[128 * 512];
        CreateFileRecord(recordZeroNumber, includeMftDataAttribute: true,
            includeAttributeList: includeAttributeList, mftSize: includeContinuation ? 8192UL : 4096UL).CopyTo(bytes, 4 * 512);
        var recordOne = CreateFileRecord(1, includeMftDataAttribute: false, includeFileName: true,
            residentData: residentFileData, nonResidentDataLength: nonResidentFileData?.Length ?? 0,
            includeMultiExtentFileData: includeMultiExtentFileData, isInUse: !deletedRecordOne);
        if (nonResidentFileData is not null)
            nonResidentFileData.AsSpan().CopyTo(bytes.AsSpan(30 * 512));
        if (firstFileDataExtent is not null)
            firstFileDataExtent.AsSpan().CopyTo(bytes.AsSpan(30 * 512));
        if (secondFileDataExtent is not null)
            secondFileDataExtent.AsSpan().CopyTo(bytes.AsSpan(32 * 512));
        recordOne.AsSpan(0, 512).CopyTo(bytes.AsSpan(6 * 512, 512));
        recordOne.AsSpan(512, 512).CopyTo(bytes.AsSpan(12 * 512, 512));
        CreateFileRecord(2, includeMftDataAttribute: false, baseFileReferenceRaw: recordTwoBaseReferenceRaw,
            includeContinuationData: includeContinuation).CopyTo(bytes, 13 * 512);
        CreateFileRecord(3, includeMftDataAttribute: false,
            baseFileReferenceRaw: includeMultiExtentFileData ? ((9UL << 48) | 1UL) : 0,
            includeFileDataContinuation: includeMultiExtentFileData).CopyTo(bytes, 15 * 512);
        if (includeContinuation)
            CreateFileRecord(4, includeMftDataAttribute: false).CopyTo(bytes, 20 * 512);
        return new MemoryImage(bytes);
    }

    private static byte[] CreateFileRecord(uint recordNumber, bool includeMftDataAttribute, bool includeFileName = false,
        bool isDirectory = false, bool isInUse = true, ulong baseFileReferenceRaw = 0, bool includeAttributeList = false,
        bool includeContinuationData = false, ulong mftSize = 4096, byte[]? residentData = null,
        int nonResidentDataLength = 0, bool includeMultiExtentFileData = false,
        bool includeFileDataContinuation = false)
    {
        var raw = new byte[1024];
        "FILE"u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4, 2), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(6, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(16, 2), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(18, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(20, 2), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(22, 2), (ushort)((isInUse ? 1 : 0) | (isDirectory ? 2 : 0)));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(28, 4), 1024);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(32, 8), baseFileReferenceRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(44, 4), recordNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(48, 2), 0x5A5A);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(50, 2), 0xAAAA);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(52, 2), 0xBBBB);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(510, 2), 0x5A5A);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(1022, 2), 0x5A5A);

        if (includeMftDataAttribute)
        {
            // Unnamed non-resident $DATA, one extent covering VCN 0..7 at LCN 4.
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56, 4), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(60, 4), 72);
            raw[64] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(72, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(80, 8), 7);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(88, 2), 64);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(96, 8), mftSize);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(104, 8), mftSize);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(112, 8), mftSize);
            // VCN 0..2 at LCN 4, then VCN 3..7 at LCN 12 (fragmented extent).
            raw[120] = 0x11; raw[121] = 0x03; raw[122] = 0x04;
            raw[123] = 0x11; raw[124] = 0x05; raw[125] = 0x08; raw[126] = 0;
            var endMarkerOffset = 128;
            if (includeAttributeList)
            {
                // Resident $ATTRIBUTE_LIST with the base extent and record 2's continuation.
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(128, 4), 0x20);
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(132, 4), 88);
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(144, 4), 64);
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(148, 2), 24);
                var list = raw.AsSpan(152, 64);
                WriteAttributeListEntry(list[..32], 0, (9UL << 48));
                WriteAttributeListEntry(list[32..], 8, (9UL << 48) | 2UL);
                endMarkerOffset = 216;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(endMarkerOffset, 4), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), checked((uint)(endMarkerOffset + 4)));
        }
        else if (includeContinuationData)
        {
            // Continuation $MFT::$DATA extent: VCN 8..15 at LCN 20.
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56, 4), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(60, 4), 72);
            raw[64] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(72, 8), 8);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(80, 8), 15);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(88, 2), 64);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(96, 8), 8192);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(104, 8), 8192);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(112, 8), 8192);
            raw[120] = 0x11; raw[121] = 0x08; raw[122] = 0x14; raw[123] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(128, 4), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), 132);
        }
        else if (includeMultiExtentFileData)
        {
            // Base $DATA extent VCN 0 at LCN 30, followed by a continuation in FILE record 3.
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56, 4), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(60, 4), 72);
            raw[64] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(70, 2), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(72, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(80, 8), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(88, 2), 64);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(96, 8), 1024);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(104, 8), 1024);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(112, 8), 1024);
            raw[120] = 0x11; raw[121] = 1; raw[122] = 30; raw[123] = 0;

            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(128, 4), 0x20);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(132, 4), 88);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(144, 4), 64);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(148, 2), 24);
            var list = raw.AsSpan(152, 64);
            WriteAttributeListEntry(list[..32], 0, (9UL << 48) | 1UL, 1);
            WriteAttributeListEntry(list[32..], 1, (9UL << 48) | 3UL, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(216, 4), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), 220);
        }
        else if (includeFileDataContinuation)
        {
            // Second $DATA extent VCN 1 at LCN 32, authenticated by FILE record 1's $ATTRIBUTE_LIST.
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56, 4), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(60, 4), 72);
            raw[64] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(70, 2), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(72, 8), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(80, 8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(88, 2), 64);
            raw[120] = 0x11; raw[121] = 1; raw[122] = 32; raw[123] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(128, 4), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), 132);
        }
        else if (residentData is not null)
        {
            var attributeLength = checked((24 + residentData.Length + 7) & ~7);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56, 4), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(60, 4), checked((uint)attributeLength));
            raw[64] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(70, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(72, 4), checked((uint)residentData.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(76, 2), 24);
            residentData.CopyTo(raw.AsSpan(80));
            var endMarkerOffset = checked(56 + attributeLength);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(endMarkerOffset, 4), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), checked((uint)(endMarkerOffset + 4)));
        }
        else if (nonResidentDataLength > 0)
        {
            // One non-resident $DATA extent: VCN 0..1 at LCN 30, 512 initialized bytes.
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56, 4), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(60, 4), 72);
            raw[64] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(72, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(80, 8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(88, 2), 64);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(96, 8), 1024);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(104, 8), checked((ulong)nonResidentDataLength));
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(112, 8), 512);
            raw[120] = 0x11; raw[121] = 2; raw[122] = 30; raw[123] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(128, 4), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), 132);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56, 4), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), 60);
        }
        if (includeFileName)
        {
            var bytesInUse = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(24, 4)));
            var endMarkerOffset = bytesInUse - sizeof(uint);
            const string fileName = "Deleted.txt";
            var nameBytes = Encoding.Unicode.GetBytes(fileName);
            var valueLength = checked(66 + nameBytes.Length);
            var attributeLength = checked((24 + valueLength + 7) & ~7);
            var newEndMarkerOffset = checked(endMarkerOffset + attributeLength);
            if (newEndMarkerOffset + sizeof(uint) > raw.Length)
                throw new InvalidOperationException("Synthetic FILE_NAME attribute exceeds the record.");

            // Move the existing end marker and append a valid resident $FILE_NAME attribute.
            raw.AsSpan(endMarkerOffset, sizeof(uint)).CopyTo(raw.AsSpan(newEndMarkerOffset, sizeof(uint)));
            var attribute = raw.AsSpan(endMarkerOffset, attributeLength);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute[..4], 0x30);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute.Slice(4, 4), checked((uint)attributeLength));
            attribute[8] = 0;
            attribute[9] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(attribute.Slice(16, 4), checked((uint)valueLength));
            BinaryPrimitives.WriteUInt16LittleEndian(attribute.Slice(20, 2), 24);
            var value = attribute.Slice(24, valueLength);
            BinaryPrimitives.WriteUInt64LittleEndian(value[..8], (9UL << 48) | 5UL);
            BinaryPrimitives.WriteUInt64LittleEndian(value.Slice(40, 8), 4096);
            BinaryPrimitives.WriteUInt64LittleEndian(value.Slice(48, 8), 2048);
            value[64] = checked((byte)fileName.Length);
            value[65] = 1;
            nameBytes.CopyTo(value[66..]);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), checked((uint)(newEndMarkerOffset + sizeof(uint))));
        }

        return raw;
    }

    private static void WriteAttributeListEntry(Span<byte> entry, ulong lowestVcn, ulong fileReference, ushort attributeId = 0)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(entry[..4], 0x80);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(4, 2), 32);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(8, 8), lowestVcn);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(16, 8), fileReference);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(24, 2), attributeId);
    }

    private sealed class MemoryImage(byte[] bytes) : IReadOnlyRandomAccessSource
    {
        public long Length => bytes.LongLength;
        public void ReadExactlyAt(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > bytes.LongLength || destination.Length > bytes.LongLength - offset)
                throw new EndOfStreamException();
            bytes.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }
        public void Dispose() { }
    }
}
