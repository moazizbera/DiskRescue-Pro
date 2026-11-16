using System.Security.Cryptography;

namespace DiskRescue.IO;

/// <summary>
/// Creates recovery evidence from bytes already held in memory.
/// This helper performs no file, directory, volume, VM, or device operations.
/// </summary>
internal static class PublicationRecoveryEvidenceFactory
{
    public static PublicationRecoveryFileEvidence FromBytes(ReadOnlySpan<byte> content)
    {
        var hash = SHA256.HashData(content);
        return new PublicationRecoveryFileEvidence(content.Length, Convert.ToHexString(hash));
    }
}
