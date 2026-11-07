using System.Text.Json;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class PhysicalDiskManifestPublisherTests
{
    [Fact]
    public async Task PublishCreatesManifestWithoutOverwriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "diskrescue-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "source.img.diskrescue.json");
            await PhysicalDiskManifestPublisher.PublishAsync(
                path,
                new { format = "test-manifest", bytes = 1024 },
                Path.Combine(directory, "source.img"),
                new string('A', 64));

            Assert.True(File.Exists(path));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("test-manifest", document.RootElement.GetProperty("format").GetString());
            Assert.Equal(1024, document.RootElement.GetProperty("bytes").GetInt32());

            await Assert.ThrowsAsync<PhysicalDiskManifestPublicationException>(() =>
                PhysicalDiskManifestPublisher.PublishAsync(
                    path,
                    new { format = "must-not-overwrite" },
                    Path.Combine(directory, "source.img"),
                    new string('B', 64)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidManifestPathStillIdentifiesPreservedVerifiedImage()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), "verified-" + Guid.NewGuid().ToString("N") + ".img");
        var digest = new string('D', 64);

        var exception = await Assert.ThrowsAsync<PhysicalDiskManifestPublicationException>(() =>
            PhysicalDiskManifestPublisher.PublishAsync(
                "invalid\0manifest.json",
                new { format = "test-manifest" },
                imagePath,
                digest));

        Assert.Equal(imagePath, exception.ImagePath);
        Assert.Equal("invalid\0manifest.json", exception.ManifestPath);
        Assert.Equal(digest, exception.Sha256);
        Assert.Contains("image has been preserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicationFailureIdentifiesPreservedVerifiedImage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "diskrescue-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var imagePath = Path.Combine(directory, "verified.img");
            await File.WriteAllBytesAsync(imagePath, new byte[] { 1, 2, 3, 4 });
            var manifestPath = Path.Combine(directory, "verified.img.diskrescue.json");
            Directory.CreateDirectory(manifestPath);
            var digest = new string('C', 64);

            var exception = await Assert.ThrowsAsync<PhysicalDiskManifestPublicationException>(() =>
                PhysicalDiskManifestPublisher.PublishAsync(
                    manifestPath,
                    new { format = "test-manifest" },
                    imagePath,
                    digest));

            Assert.Equal(imagePath, exception.ImagePath);
            Assert.Equal(manifestPath, exception.ManifestPath);
            Assert.Equal(digest, exception.Sha256);
            Assert.True(File.Exists(imagePath));
            Assert.Contains("image has been preserved", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
