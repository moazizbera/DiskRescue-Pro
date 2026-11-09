using System.Security.Cryptography;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class DeletedRecoveryReconcilerTests
{
    [Fact]
    public void FinalizesOnlyWhenOutputLengthAndHashMatchPendingManifest()
    {
        var directory = CreateDirectory();
        try
        {
            var artifacts = CreateArtifacts(directory, "verified recovered bytes");
            var finalManifest = DeletedRecoveryReconciler.FinalizePendingManifest(artifacts.PendingManifest);

            Assert.Equal(artifacts.FinalManifest, finalManifest);
            Assert.True(File.Exists(artifacts.Output));
            Assert.True(File.Exists(finalManifest));
            Assert.False(File.Exists(artifacts.PendingManifest));
            Assert.True(File.Exists(artifacts.Lock));
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(finalManifest));
            Assert.Equal("bounded-deleted-ntfs-data-stream-export",
                document.RootElement.GetProperty("operation").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void HashMismatchPreservesOutputPendingManifestAndLock()
    {
        var directory = CreateDirectory();
        try
        {
            var artifacts = CreateArtifacts(directory, "original recovered bytes");
            File.WriteAllText(artifacts.Output, "tampered bytes");

            Assert.Throws<IOException>(() =>
                DeletedRecoveryReconciler.FinalizePendingManifest(artifacts.PendingManifest));

            Assert.True(File.Exists(artifacts.Output));
            Assert.True(File.Exists(artifacts.PendingManifest));
            Assert.True(File.Exists(artifacts.Lock));
            Assert.False(File.Exists(artifacts.FinalManifest));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ExistingFinalManifestIsNeverOverwritten()
    {
        var directory = CreateDirectory();
        try
        {
            var artifacts = CreateArtifacts(directory, "verified bytes");
            File.WriteAllText(artifacts.FinalManifest, "preserve this manifest");

            Assert.Throws<IOException>(() =>
                DeletedRecoveryReconciler.FinalizePendingManifest(artifacts.PendingManifest));

            Assert.Equal("preserve this manifest", File.ReadAllText(artifacts.FinalManifest));
            Assert.True(File.Exists(artifacts.PendingManifest));
            Assert.True(File.Exists(artifacts.Lock));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void InvalidOperationLockPreservesEveryArtifact()
    {
        var directory = CreateDirectory();
        try
        {
            var artifacts = CreateArtifacts(directory, "verified bytes");
            File.WriteAllText(artifacts.Lock, "not the expected operation marker");

            Assert.Throws<IOException>(() =>
                DeletedRecoveryReconciler.FinalizePendingManifest(artifacts.PendingManifest));

            Assert.True(File.Exists(artifacts.Output));
            Assert.True(File.Exists(artifacts.PendingManifest));
            Assert.True(File.Exists(artifacts.Lock));
            Assert.False(File.Exists(artifacts.FinalManifest));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MissingOutputPreservesPendingEvidence()
    {
        var directory = CreateDirectory();
        try
        {
            var artifacts = CreateArtifacts(directory, "verified bytes");
            File.Delete(artifacts.Output);

            Assert.Throws<IOException>(() =>
                DeletedRecoveryReconciler.FinalizePendingManifest(artifacts.PendingManifest));

            Assert.True(File.Exists(artifacts.PendingManifest));
            Assert.True(File.Exists(artifacts.Lock));
            Assert.False(File.Exists(artifacts.FinalManifest));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static (string Output, string PendingManifest, string FinalManifest, string Lock)
        CreateArtifacts(string directory, string payload)
    {
        var token = Guid.NewGuid().ToString("N");
        var outputName = "recovered.bin";
        var output = Path.Combine(directory, outputName);
        var pending = Path.Combine(directory, $".diskrescue-{token}.manifest.partial");
        var finalManifest = output + ".recovery.json";
        var lockPath = Path.Combine(directory, $".diskrescue-{token}.lock");
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        File.WriteAllBytes(output, bytes);
        File.WriteAllText(lockPath, "DiskRescuePro.DeletedExport.v1\n" + token);
        var manifest = $$"""
        {
          "manifestVersion": 1,
          "operation": "bounded-deleted-ntfs-data-stream-export",
          "export": {
            "fileName": "{{outputName}}",
            "fullPath": "{{output.Replace("\\", "\\\\")}}",
            "bytesWritten": {{bytes.Length}},
            "sha256": "{{hash}}"
          }
        }
        """;
        File.WriteAllText(pending, manifest);
        return (output, pending, finalManifest, lockPath);
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DiskRescue-Reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
