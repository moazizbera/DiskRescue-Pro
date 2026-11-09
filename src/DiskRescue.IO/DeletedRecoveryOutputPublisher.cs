namespace DiskRescue.IO;

/// <summary>
/// Publishes a recovered data file and its provenance manifest as a pair.
/// If manifest publication fails after the data file is moved, the newly
/// published data file is rolled back. Existing destinations are never replaced.
/// </summary>
public static class DeletedRecoveryOutputPublisher
{
    public static void PublishPair(
        string temporaryOutputPath,
        string outputPath,
        string temporaryManifestPath,
        string manifestPath) =>
        PublishPair(temporaryOutputPath, outputPath, temporaryManifestPath, manifestPath,
            File.Move, File.Delete);

    internal static void PublishPair(
        string temporaryOutputPath,
        string outputPath,
        string temporaryManifestPath,
        string manifestPath,
        Action<string, string> moveFile,
        Action<string> deleteFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(moveFile);
        ArgumentNullException.ThrowIfNull(deleteFile);

        if (File.Exists(outputPath) || Directory.Exists(outputPath))
            throw new IOException("Recovery output already exists; existing files are never overwritten.");
        if (File.Exists(manifestPath) || Directory.Exists(manifestPath))
            throw new IOException("Recovery manifest already exists; existing files are never overwritten.");

        var outputPublished = false;
        try
        {
            moveFile(temporaryOutputPath, outputPath);
            outputPublished = true;
            moveFile(temporaryManifestPath, manifestPath);
        }
        catch (Exception publishException)
        {
            if (outputPublished)
            {
                try
                {
                    deleteFile(outputPath);
                }
                catch (Exception rollbackException)
                {
                    throw new IOException(
                        "Recovery manifest publication failed and the newly published output could not be rolled back.",
                        new AggregateException(publishException, rollbackException));
                }
            }

            throw;
        }
    }
}
