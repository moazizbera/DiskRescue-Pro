namespace DiskRescue.IO;

/// <summary>
/// Assembles a recovery snapshot from explicitly supplied in-memory entries.
/// This is a fixture/support helper, not a filesystem collector: it does not
/// discover names, read files, enumerate directories, or access devices.
/// </summary>
internal static class PublicationRecoveryEvidenceSnapshotFactory
{
    public static PublicationRecoverySnapshot FromInMemoryEntries(
        PhysicalAcquisitionPublicationCheckpoint checkpoint,
        ReadOnlyMemory<byte>? imageFinal = null,
        ReadOnlyMemory<byte>? manifestFinal = null,
        ReadOnlyMemory<byte>? imageTemporary = null,
        ReadOnlyMemory<byte>? manifestTemporary = null) =>
        new(
            checkpoint,
            CreateEvidence(imageFinal),
            CreateEvidence(manifestFinal),
            CreateEvidence(imageTemporary),
            CreateEvidence(manifestTemporary));

    private static PublicationRecoveryFileEvidence? CreateEvidence(ReadOnlyMemory<byte>? content) =>
        content is null ? null : PublicationRecoveryEvidenceFactory.FromBytes(content.Value.Span);
}
