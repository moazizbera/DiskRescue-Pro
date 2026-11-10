using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class DeletedRecoveryOutputPublisherTests
{
    [Fact]
    public void ManifestPublicationFailureRollsBackOnlyTheNewlyPublishedOutput()
    {
        var directory = CreateDirectory();
        try
        {
            var temporaryOutput = Path.Combine(directory, "output.partial");
            var output = Path.Combine(directory, "recovered.bin");
            var temporaryManifest = Path.Combine(directory, "manifest.partial");
            var manifest = Path.Combine(directory, "recovered.bin.recovery.json");
            File.WriteAllText(temporaryOutput, "recovered bytes");
            File.WriteAllText(temporaryManifest, "manifest");

            void Move(string source, string destination)
            {
                if (string.Equals(destination, manifest, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("injected manifest publication failure");
                File.Move(source, destination);
            }

            Assert.Throws<IOException>(() => DeletedRecoveryOutputPublisher.PublishPair(
                temporaryOutput, output, temporaryManifest, manifest, Move, File.Delete));

            Assert.False(File.Exists(output));
            Assert.False(File.Exists(manifest));
            Assert.True(File.Exists(temporaryManifest));
            Assert.False(File.Exists(temporaryOutput));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OutputPublicationFailureDoesNotCreateManifestOrDeleteExistingFiles()
    {
        var directory = CreateDirectory();
        try
        {
            var temporaryOutput = Path.Combine(directory, "output.partial");
            var output = Path.Combine(directory, "recovered.bin");
            var temporaryManifest = Path.Combine(directory, "manifest.partial");
            var manifest = Path.Combine(directory, "recovered.bin.recovery.json");
            File.WriteAllText(temporaryOutput, "recovered bytes");
            File.WriteAllText(temporaryManifest, "manifest");

            static void Move(string source, string destination) =>
                throw new IOException("injected output publication failure");

            Assert.Throws<IOException>(() => DeletedRecoveryOutputPublisher.PublishPair(
                temporaryOutput, output, temporaryManifest, manifest, Move, File.Delete));

            Assert.False(File.Exists(output));
            Assert.False(File.Exists(manifest));
            Assert.True(File.Exists(temporaryOutput));
            Assert.True(File.Exists(temporaryManifest));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RollbackFailureIsSurfacedInsteadOfReportedAsCleanFailure()
    {
        var directory = CreateDirectory();
        try
        {
            var temporaryOutput = Path.Combine(directory, "output.partial");
            var output = Path.Combine(directory, "recovered.bin");
            var temporaryManifest = Path.Combine(directory, "manifest.partial");
            var manifest = Path.Combine(directory, "recovered.bin.recovery.json");
            File.WriteAllText(temporaryOutput, "recovered bytes");
            File.WriteAllText(temporaryManifest, "manifest");

            void Move(string source, string destination)
            {
                if (string.Equals(destination, manifest, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("injected manifest publication failure");
                File.Move(source, destination);
            }

            var exception = Assert.Throws<IOException>(() =>
                DeletedRecoveryOutputPublisher.PublishPair(
                    temporaryOutput, output, temporaryManifest, manifest, Move,
                    _ => throw new UnauthorizedAccessException("injected rollback failure")));

            Assert.Contains("could not be rolled back", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<AggregateException>(exception.InnerException);
            Assert.True(File.Exists(output));
            Assert.False(File.Exists(manifest));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExistingOutputOrManifestIsNeverOverwritten()
    {
        var directory = CreateDirectory();
        try
        {
            var temporaryOutput = Path.Combine(directory, "output.partial");
            var output = Path.Combine(directory, "recovered.bin");
            var temporaryManifest = Path.Combine(directory, "manifest.partial");
            var manifest = Path.Combine(directory, "recovered.bin.recovery.json");
            File.WriteAllText(temporaryOutput, "temporary output");
            File.WriteAllText(temporaryManifest, "temporary manifest");
            File.WriteAllText(output, "preserve output");

            Assert.Throws<IOException>(() => DeletedRecoveryOutputPublisher.PublishPair(
                temporaryOutput, output, temporaryManifest, manifest));
            Assert.Equal("preserve output", File.ReadAllText(output));

            File.Delete(output);
            File.WriteAllText(manifest, "preserve manifest");
            Assert.Throws<IOException>(() => DeletedRecoveryOutputPublisher.PublishPair(
                temporaryOutput, output, temporaryManifest, manifest));
            Assert.Equal("preserve manifest", File.ReadAllText(manifest));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DiskRescue-Publisher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
