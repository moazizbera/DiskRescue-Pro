using System.Diagnostics;
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
    public void CleanupPreservesManifestWhenOutputMayAlreadyHaveBeenPublished()
    {
        var directory = CreateDirectory();
        try
        {
            var token = Guid.NewGuid().ToString("N");
            var lockPath = Path.Combine(directory, $".diskrescue-{token}.lock");
            var manifestPath = Path.Combine(directory, $".diskrescue-{token}.manifest.partial");
            var possiblePublishedOutput = Path.Combine(directory, "recovered.bin");
            File.WriteAllText(lockPath, "DiskRescuePro.DeletedExport.v1\\n" + token);
            File.WriteAllText(manifestPath, "pending manifest");
            File.WriteAllText(possiblePublishedOutput, "published output");

            Assert.Equal(0, DeletedRecoveryTemporaryFileLease.CleanupStale(directory));
            Assert.True(File.Exists(lockPath));
            Assert.Equal("pending manifest", File.ReadAllText(manifestPath));
            Assert.Equal("published output", File.ReadAllText(possiblePublishedOutput));
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

    [Fact]
    public void ChildProcessTerminationLeavesSafelyCleanableTemporaryFiles()
    {
        var childDirectory = Environment.GetEnvironmentVariable("DISKRESCUE_LEASE_CHILD_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(childDirectory))
        {
            using var childLease = DeletedRecoveryTemporaryFileLease.Create(childDirectory);
            File.WriteAllText(childLease.OutputPath, "interrupted output");
            File.WriteAllText(childLease.ManifestPath, "interrupted manifest");
            var signalPath = Environment.GetEnvironmentVariable("DISKRESCUE_LEASE_CHILD_SIGNAL")
                ?? throw new InvalidOperationException("Child signal path was not supplied.");
            File.WriteAllText(signalPath, "ready");
            Thread.Sleep(Timeout.Infinite);
            return;
        }

        var directory = CreateDirectory();
        var signal = Path.Combine(directory, "child-ready.signal");
        var projectPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../DiskRescue.IO.Tests.csproj"));
        Assert.True(File.Exists(projectPath), $"Test project was not found: {projectPath}");

        using var child = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(projectPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        child.StartInfo.ArgumentList.Add("test");
        child.StartInfo.ArgumentList.Add(projectPath);
        child.StartInfo.ArgumentList.Add("--configuration");
        child.StartInfo.ArgumentList.Add("Release");
        child.StartInfo.ArgumentList.Add("--no-build");
        child.StartInfo.ArgumentList.Add("--no-restore");
        child.StartInfo.ArgumentList.Add("--filter");
        child.StartInfo.ArgumentList.Add(
            "FullyQualifiedName=DiskRescue.IO.Tests.DeletedRecoveryTemporaryFileLeaseTests.ChildProcessTerminationLeavesSafelyCleanableTemporaryFiles");
        child.StartInfo.Environment["DISKRESCUE_LEASE_CHILD_DIRECTORY"] = directory;
        child.StartInfo.Environment["DISKRESCUE_LEASE_CHILD_SIGNAL"] = signal;

        try
        {
            Assert.True(child.Start(), "Could not start the isolated lease-owner test process.");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(signal) && !child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(30))
                Thread.Sleep(25);

            if (!File.Exists(signal))
            {
                var childOutput = child.HasExited ? child.StandardOutput.ReadToEnd() : string.Empty;
                var childError = child.HasExited ? child.StandardError.ReadToEnd() : string.Empty;
                Assert.Fail(child.HasExited
                    ? $"Child test process exited before acquiring its lease (exit {child.ExitCode}). Output: {childOutput} Error: {childError}"
                    : "Child test process did not acquire its lease before the timeout.");
            }

            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(15000), "Child test process did not terminate.");
            File.Delete(signal);

            var cleanupDeadline = Stopwatch.StartNew();
            var removed = 0;
            while (removed == 0 && cleanupDeadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                removed = DeletedRecoveryTemporaryFileLease.CleanupStale(directory);
                if (removed == 0)
                    Thread.Sleep(50);
            }

            Assert.Equal(1, removed);
            Assert.False(File.Exists(signal));
            var remainingLeaseArtifacts = Directory.EnumerateFiles(directory)
                .Where(path => Path.GetFileName(path).StartsWith(".diskrescue-", StringComparison.Ordinal))
                .ToArray();
            Assert.Empty(remainingLeaseArtifacts);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(15000);
            }
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
