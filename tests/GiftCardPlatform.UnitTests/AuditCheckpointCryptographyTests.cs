using System.Security.Cryptography;
using GiftCardPlatform.Modules.Audit.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class AuditCheckpointCryptographyTests
{
    [Fact]
    public void Manifest_digest_is_deterministic_and_commits_to_every_boundary_value()
    {
        var id = Guid.Parse("019c0598-6700-7000-8000-000000000101");
        var previousId = Guid.Parse("019c0598-6700-7000-8000-000000000100");
        var previousDigest = SHA256.HashData("previous"u8);
        var root = SHA256.HashData("root"u8);
        var createdAt = DateTimeOffset.Parse(
            "2026-08-06T08:00:00.1234560+00:00",
            System.Globalization.CultureInfo.InvariantCulture);

        var first = AuditCheckpointCryptography.ComputeManifestDigest(
            id, previousId, previousDigest, 11, 19, 9, root, createdAt);
        var second = AuditCheckpointCryptography.ComputeManifestDigest(
            id, previousId, previousDigest, 11, 19, 9, root, createdAt);
        var changedRoot = root.ToArray();
        changedRoot[0] ^= 0xff;
        var changed = AuditCheckpointCryptography.ComputeManifestDigest(
            id, previousId, previousDigest, 11, 19, 9, changedRoot, createdAt);

        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void Checkpoint_canonicalizes_database_time_precision_before_hashing()
    {
        var root = SHA256.HashData("root"u8);
        var timeWithSubMicrosecondTicks = new DateTimeOffset(
            638901792001234567,
            TimeSpan.Zero);

        var checkpoint = AuditCheckpoint.Create(
            null, null, 1, 1, 1, root, timeWithSubMicrosecondTicks);
        var recomputed = AuditCheckpointCryptography.ComputeManifestDigest(
            checkpoint.Id,
            checkpoint.PreviousCheckpointId,
            checkpoint.PreviousManifestDigest,
            checkpoint.FirstSequence,
            checkpoint.LastSequence,
            checkpoint.RecordCount,
            checkpoint.MerkleRoot,
            checkpoint.CreatedAtUtc);

        Assert.Equal(0, checkpoint.CreatedAtUtc.UtcTicks % 10);
        Assert.Equal(checkpoint.ManifestDigest, recomputed);
    }

    [Fact]
    public void Ecdsa_p256_signature_verifies_and_changed_signature_does_not()
    {
        var checkpoint = AuditCheckpoint.Create(
            null,
            null,
            1,
            1,
            1,
            SHA256.HashData("root"u8),
            DateTimeOffset.Parse(
                "2026-08-06T08:00:00+00:00",
                System.Globalization.CultureInfo.InvariantCulture));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var signature = key.SignHash(
            checkpoint.ManifestDigest,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var valid = new AuditCheckpointSeal(
            checkpoint.Id,
            AuditCheckpointCryptography.SignatureAlgorithm,
            "test-key",
            publicKey,
            signature,
            DateTimeOffset.UtcNow);

        Assert.True(AuditCheckpointCryptography.VerifySignature(checkpoint, valid));

        signature[0] ^= 0xff;
        var changed = new AuditCheckpointSeal(
            checkpoint.Id,
            AuditCheckpointCryptography.SignatureAlgorithm,
            "test-key",
            publicKey,
            signature,
            DateTimeOffset.UtcNow);
        Assert.False(AuditCheckpointCryptography.VerifySignature(checkpoint, changed));
    }
}
