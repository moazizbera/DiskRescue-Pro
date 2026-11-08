using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class DeletedRecoveryTemporaryFileLeaseTests
{
    [Fact]
    public void CleanupRemovesOnlyArtifactsOwnedByAnAbandonedOperation()
    {
        var directory = CreateDirectory();
        try
        {
            var token = Guid.NewGuid().ToString("N");
            var lockPath = Path.Combine(directory, $".diskrescue-{token}.lock");
            var outputPath = Path.Combine(directory, $".diskrescue-{token}.output.partial");
            var manifestPath = Path.Combine(directory, $".diskrescue-{token}.manifest.partial");
            File.WriteAllText(lockPath, "DiskRescuePro.DeletedExport.v1\n" + token);
            File.WriteAllText(outputPath, "interrupted output");
            File.WriteAllText(manifestPath, "interrupted manifest");
            var unrelated = Path.Combine(directory, ".other.partial");
            File.WriteAllText(unrelated, "preserve");

            var removed = DeletedRecoveryTemporaryFileLease.CleanupStale(directory);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(lockPath));
            Assert.False(File.Exists(outputPath));
            Assert.False(File.Exists(manifestPath));
            Assert.Equal("preserve", File.ReadAllText(unrelated));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CleanupDoesNotTouchAnActiveOperation()
    {
        var directory = CreateDirectory();
        try
        {
            using var lease = DeletedRecoveryTemporaryFileLease.Create(directory);
            File.WriteAllText(lease.OutputPath, "active output");
            File.WriteAllText(lease.ManifestPath, "active manifest");

            Assert.Equal(0, DeletedRecoveryTemporaryFileLease.CleanupStale(directory));
            Assert.Equal("active output", File.ReadAllText(lease.OutputPath));
            Assert.Equal("active manifest", File.ReadAllText(lease.ManifestPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LeaseDisposalRemovesItsOwnTemporaryFilesAndLock()
    {
        var directory = CreateDirectory();
        string output;
        string manifest;
        try
        {
            using (var lease = DeletedRecoveryTemporaryFileLease.Create(directory))
            {
                output = lease.OutputPath;
                manifest = lease.ManifestPath;
                File.WriteAllText(output, "temporary output");
                File.WriteAllText(manifest, "temporary manifest");
            }

            Assert.False(File.Exists(output));
            Assert.False(File.Exists(manifest));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CleanupIgnoresMalformedLockMarkers()
    {
        var directory = CreateDirectory();
        try
        {
            var token = Guid.NewGuid().ToString("N");
            var lockPath = Path.Combine(directory, $".diskrescue-{token}.lock");
            var outputPath = Path.Combine(directory, $".diskrescue-{token}.output.partial");
            File.WriteAllText(lockPath, "not a DiskRescue operation");
            File.WriteAllText(outputPath, "preserve");

            Assert.Equal(0, DeletedRecoveryTemporaryFileLease.CleanupStale(directory));
            Assert.True(File.Exists(lockPath));
            Assert.Equal("preserve", File.ReadAllText(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DiskRescue-Lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
