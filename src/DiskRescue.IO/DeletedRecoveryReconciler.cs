using System.Security.Cryptography;
using System.Text.Json;

namespace DiskRescue.IO;

/// <summary>
/// Finalizes an interrupted deleted-export publication only when its preserved
/// operation lock and manifest identify an existing output whose length and SHA-256
/// exactly match the manifest. This verifies exported bytes, not original content.
/// </summary>
public static class DeletedRecoveryReconciler
{
    private const string LockPrefix = ".diskrescue-";
    private const string LockSuffix = ".lock";
    private const string ManifestSuffix = ".manifest.partial";
    private const string MarkerPrefix = "DiskRescuePro.DeletedExport.v1\n";
    private const int MaximumManifestBytes = 1024 * 1024;

    public static string FinalizePendingManifest(string pendingManifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingManifestPath);
        var pendingPath = Path.GetFullPath(pendingManifestPath);
        var directory = Path.GetDirectoryName(pendingPath)
            ?? throw new IOException("Pending manifest must have a parent directory.");
        var fileName = Path.GetFileName(pendingPath);

        if (!fileName.StartsWith(LockPrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(ManifestSuffix, StringComparison.Ordinal))
            throw new IOException("Pending manifest filename is not a DiskRescue Pro interrupted-export artifact.");

        var token = fileName.Substring(LockPrefix.Length, fileName.Length - LockPrefix.Length - ManifestSuffix.Length);
        if (!Guid.TryParseExact(token, "N", out _))
            throw new IOException("Pending manifest operation identifier is invalid.");

        var lockPath = Path.Combine(directory, LockPrefix + token + LockSuffix);
        if (!File.Exists(lockPath) || !File.Exists(pendingPath))
            throw new IOException("The matching operation lock or pending manifest is missing; no changes were made.");
        RejectReparsePoint(lockPath, "operation lock");
        RejectReparsePoint(pendingPath, "pending manifest");

        using var operationLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using (var reader = new StreamReader(operationLock, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true))
        {
            var marker = reader.ReadToEnd();
            if (!string.Equals(marker, MarkerPrefix + token, StringComparison.Ordinal))
                throw new IOException("Operation-lock marker does not match this pending manifest; no changes were made.");
        }

        var manifestInfo = new FileInfo(pendingPath);
        if (manifestInfo.Length is <= 0 or > MaximumManifestBytes)
            throw new IOException("Pending manifest size is outside the supported 1-byte to 1-MiB range.");

        var manifestBytes = File.ReadAllBytes(pendingPath);
        using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("manifestVersion", out var version) || version.GetInt32() != 1 ||
            !root.TryGetProperty("operation", out var operation) ||
            operation.GetString() != "bounded-deleted-ntfs-data-stream-export" ||
            !root.TryGetProperty("export", out var export) || export.ValueKind != JsonValueKind.Object)
            throw new IOException("Pending manifest is not a supported DiskRescue Pro deleted-export manifest.");

        var outputName = export.GetProperty("fileName").GetString();
        var recordedPath = export.GetProperty("fullPath").GetString();
        var bytesWritten = export.GetProperty("bytesWritten").GetUInt64();
        var expectedHashText = export.GetProperty("sha256").GetString();

        if (string.IsNullOrWhiteSpace(outputName) || Path.GetFileName(outputName) != outputName ||
            string.IsNullOrWhiteSpace(recordedPath) || bytesWritten > long.MaxValue ||
            string.IsNullOrWhiteSpace(expectedHashText) || expectedHashText.Length != 64)
            throw new IOException("Pending manifest contains invalid output identity or hash metadata.");

        byte[] expectedHash;
        try { expectedHash = Convert.FromHexString(expectedHashText); }
        catch (FormatException exception) { throw new IOException("Pending manifest SHA-256 is malformed.", exception); }

        var outputPath = Path.GetFullPath(Path.Combine(directory, outputName));
        if (!string.Equals(Path.GetFullPath(recordedPath), outputPath, PathComparison))
            throw new IOException("Manifest output path does not resolve to the pending manifest's directory and filename.");

        var finalManifestPath = outputPath + ".recovery.json";
        if (File.Exists(finalManifestPath) || Directory.Exists(finalManifestPath))
            throw new IOException("Final manifest already exists; reconciliation never overwrites existing files.");
        if (!File.Exists(outputPath))
            throw new IOException("Expected final output is missing; pending evidence was preserved.");

        RejectReparsePoint(outputPath, "recovered output");
        // Keep the verified output open without write/delete sharing until the
        // manifest move completes, preventing replacement during finalization.
        using var output = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if ((ulong)output.Length != bytesWritten)
            throw new IOException("Recovered output length does not match the pending manifest; evidence was preserved.");
        var actualHash = SHA256.HashData(output);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new IOException("Recovered output SHA-256 does not match the pending manifest; evidence was preserved.");

        // Recheck the no-overwrite condition immediately before publication.
        if (File.Exists(finalManifestPath) || Directory.Exists(finalManifestPath))
            throw new IOException("Final manifest appeared during reconciliation; no existing file was overwritten.");

        File.Move(pendingPath, finalManifestPath, overwrite: false);
        return finalManifestPath;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The {description} must not be a reparse point.");
    }
}
