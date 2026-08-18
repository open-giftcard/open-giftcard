namespace GiftCardPlatform.Modules.Audit.Domain;

/// <summary>
/// Immutable manifest committing to one complete sequence range. Signature and
/// witness are separate append-only records so external calls never run while
/// the audit-boundary lock or a business transaction is held (ADR-013).
/// </summary>
internal sealed class AuditCheckpoint
{
    private AuditCheckpoint()
    {
        MerkleRoot = null!;
        ManifestDigest = null!;
        HashAlgorithm = null!;
    }

    private AuditCheckpoint(
        Guid id,
        Guid? previousCheckpointId,
        byte[]? previousManifestDigest,
        long firstSequence,
        long lastSequence,
        int recordCount,
        byte[] merkleRoot,
        byte[] manifestDigest,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        PreviousCheckpointId = previousCheckpointId;
        PreviousManifestDigest = previousManifestDigest;
        FirstSequence = firstSequence;
        LastSequence = lastSequence;
        RecordCount = recordCount;
        MerkleRoot = merkleRoot;
        ManifestDigest = manifestDigest;
        CreatedAtUtc = createdAtUtc;
        FormatVersion = 1;
        HashAlgorithm = "SHA-256";
    }

    public Guid Id { get; private set; }

    public Guid? PreviousCheckpointId { get; private set; }

    public byte[]? PreviousManifestDigest { get; private set; }

    public long FirstSequence { get; private set; }

    public long LastSequence { get; private set; }

    public int RecordCount { get; private set; }

    public byte[] MerkleRoot { get; private set; }

    public byte[] ManifestDigest { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public int FormatVersion { get; private set; }

    public string HashAlgorithm { get; private set; }

    public static AuditCheckpoint Create(
        Guid? previousCheckpointId,
        byte[]? previousManifestDigest,
        long firstSequence,
        long lastSequence,
        int recordCount,
        byte[] merkleRoot,
        DateTimeOffset createdAtUtc)
    {
        if (recordCount < 1 || firstSequence < 1 || lastSequence < firstSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(recordCount));
        }

        ArgumentNullException.ThrowIfNull(merkleRoot);
        if (merkleRoot.Length != 32 || previousManifestDigest is { Length: not 32 })
        {
            throw new ArgumentException("Checkpoint digests must be SHA-256 values.");
        }

        // PostgreSQL timestamptz has microsecond precision. Canonicalize before
        // hashing so a manifest read after persistence has the same digest as
        // the in-memory manifest that was originally written.
        var utc = createdAtUtc.ToUniversalTime();
        var canonicalCreatedAtUtc = new DateTimeOffset(
            utc.UtcTicks - (utc.UtcTicks % 10),
            TimeSpan.Zero);
        var id = Guid.CreateVersion7();
        var digest = AuditCheckpointCryptography.ComputeManifestDigest(
            id,
            previousCheckpointId,
            previousManifestDigest,
            firstSequence,
            lastSequence,
            recordCount,
            merkleRoot,
            canonicalCreatedAtUtc);

        return new AuditCheckpoint(
            id,
            previousCheckpointId,
            previousManifestDigest?.ToArray(),
            firstSequence,
            lastSequence,
            recordCount,
            merkleRoot.ToArray(),
            digest,
            canonicalCreatedAtUtc);
    }
}

internal sealed class AuditCheckpointSeal
{
    private AuditCheckpointSeal()
    {
        Algorithm = null!;
        KeyId = null!;
        PublicKey = null!;
        Signature = null!;
    }

    public AuditCheckpointSeal(
        Guid checkpointId,
        string algorithm,
        string keyId,
        byte[] publicKey,
        byte[] signature,
        DateTimeOffset signedAtUtc)
    {
        CheckpointId = checkpointId;
        Algorithm = algorithm;
        KeyId = keyId;
        PublicKey = publicKey.ToArray();
        Signature = signature.ToArray();
        SignedAtUtc = signedAtUtc;
    }

    public Guid CheckpointId { get; private set; }

    public string Algorithm { get; private set; }

    public string KeyId { get; private set; }

    public byte[] PublicKey { get; private set; }

    public byte[] Signature { get; private set; }

    public DateTimeOffset SignedAtUtc { get; private set; }
}

internal sealed class AuditCheckpointWitness
{
    private AuditCheckpointWitness()
    {
        Reference = null!;
        ManifestDigest = null!;
    }

    public AuditCheckpointWitness(
        Guid checkpointId,
        string reference,
        byte[] manifestDigest,
        DateTimeOffset witnessedAtUtc)
    {
        CheckpointId = checkpointId;
        Reference = reference;
        ManifestDigest = manifestDigest.ToArray();
        WitnessedAtUtc = witnessedAtUtc;
    }

    public Guid CheckpointId { get; private set; }

    public string Reference { get; private set; }

    public byte[] ManifestDigest { get; private set; }

    public DateTimeOffset WitnessedAtUtc { get; private set; }
}
