using DiskRescue.IO;

namespace DiskRescue.Filesystems;

public sealed record NtfsMftEntry(
    ulong RecordNumber,
    ushort SequenceNumber,
    ulong ParentRecordNumber,
    ushort ParentSequenceNumber,
    string Name,
    byte Namespace,
    bool IsDirectory,
    ulong AllocatedSize,
    ulong RealSize,
    uint FileAttributes,
    bool IsDeleted = false);

public sealed record NtfsMftScanResult(
    bool IsValid,
    bool WasCancelled,
    IReadOnlyList<NtfsMftEntry> Entries,
    ulong RecordsExamined,
    ulong RecordsSkipped,
    IReadOnlyList<string> Issues);

/// <summary>
/// Bounded flat scan of records covered by the initial validated MFT data extent.
/// Does not follow attribute lists or directory indexes and does not infer a hierarchy.
/// </summary>
public sealed partial class NtfsMftReader
{
    public NtfsMftScanResult ScanRecords(int maximumRecords = 10000, CancellationToken cancellationToken = default, bool includeDeleted = false)
    {
        if (maximumRecords is < 1 or > 65536)
            return new(false, false, Array.Empty<NtfsMftEntry>(), 0, 0,
                new[] { "Maximum records must be between 1 and 65536." });

        var readableBytes = Math.Min(_mftInitializedSize, _mftMappedBytes);
        var availableRecords = readableBytes / _boot.FileRecordSizeBytes;
        var scanCount = Math.Min((ulong)maximumRecords, availableRecords);
        var entries = new List<NtfsMftEntry>();
        var issues = new List<string>();
        ulong examined = 0;
        ulong skipped = 0;
        var cancelled = false;

        for (ulong recordNumber = 0; recordNumber < scanCount; recordNumber++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            examined++;
            var read = ReadRecord(recordNumber);
            if (!read.IsValid || read.Record is null)
            {
                skipped++;
                AddIssue($"MFT record {recordNumber}: {string.Join("; ", read.Issues)}");
                continue;
            }

            var record = read.Record;
            var isDeleted = (record.Flags & 0x0001) == 0;
            if (isDeleted && !includeDeleted)
                continue;

            var parsedAttributes = NtfsAttributeParser.Parse(record);
            if (!parsedAttributes.IsValid)
            {
                skipped++;
                AddIssue($"MFT record {recordNumber} attributes: {string.Join("; ", parsedAttributes.Issues)}");
                continue;
            }

            var fileNameAttributes = parsedAttributes.Attributes
                .Where(a => a.Type == 0x30 && !a.IsNonResident)
                .ToArray();
            if (fileNameAttributes.Length == 0)
            {
                AddIssue($"MFT record {recordNumber} is in use but has no resident $FILE_NAME attribute.");
                continue;
            }

            foreach (var attribute in fileNameAttributes)
            {
                var parsedName = NtfsFileNameParser.Parse(attribute);
                if (!parsedName.IsValid || parsedName.FileName is null)
                {
                    AddIssue($"MFT record {recordNumber} $FILE_NAME: {string.Join("; ", parsedName.Issues)}");
                    continue;
                }

                var fileName = parsedName.FileName;
                entries.Add(new NtfsMftEntry(
                    recordNumber,
                    record.SequenceNumber,
                    fileName.ParentRecordNumber,
                    fileName.ParentSequenceNumber,
                    fileName.Name,
                    fileName.Namespace,
                    (record.Flags & 0x0002) != 0,
                    fileName.AllocatedSize,
                    fileName.RealSize,
                    fileName.FileAttributes,
                    isDeleted));
            }
        }

        return new(true, cancelled, entries, examined, skipped, issues);

        void AddIssue(string issue)
        {
            if (issues.Count < 100)
                issues.Add(issue);
        }
    }
}
