using System.Collections.Concurrent;
using System.Security.Cryptography;
using GiftCardPlatform.Modules.Audit.Contracts;

namespace GiftCardPlatform.IntegrationTests;

internal sealed class TestAuditCheckpointSigner : IAuditCheckpointSigner, IDisposable
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Task<AuditCheckpointSignature> SignDigestAsync(
        ReadOnlyMemory<byte> digest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var signature = key.SignHash(
            digest.Span,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Task.FromResult(new AuditCheckpointSignature(
            "ECDSA-P256-SHA256-P1363",
            Convert.ToHexString(SHA256.HashData(publicKey)),
            publicKey,
            signature));
    }

    public void Dispose() => key.Dispose();
}

internal sealed class TestAuditCheckpointWitness : IAuditCheckpointWitness
{
    private readonly ConcurrentDictionary<string, byte[]> manifests =
        new(StringComparer.Ordinal);

    public Task<AuditCheckpointWitnessReceipt> PublishAsync(
        Guid checkpointId,
        ReadOnlyMemory<byte> signedManifest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reference = $"{checkpointId:N}.json";
        var bytes = signedManifest.ToArray();
        manifests.AddOrUpdate(
            reference,
            bytes,
            (_, existing) => CryptographicOperations.FixedTimeEquals(existing, bytes)
                ? existing
                : throw new InvalidOperationException("A checkpoint reference is immutable."));
        return Task.FromResult(new AuditCheckpointWitnessReceipt(
            reference,
            DateTimeOffset.UtcNow));
    }

    public Task<byte[]?> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            manifests.TryGetValue(reference, out var bytes) ? bytes.ToArray() : null);
    }

    public Task<IReadOnlyCollection<string>> ListReferencesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyCollection<string> references = manifests.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(references);
    }

    public byte[] ReplaceForTest(string reference, byte[] replacement)
    {
        var original = manifests[reference];
        manifests[reference] = replacement.ToArray();
        return original;
    }

    public void AddForTest(string reference, byte[] manifest) =>
        AssertAdded(manifests.TryAdd(reference, manifest.ToArray()));

    public void RemoveForTest(string reference) => manifests.TryRemove(reference, out _);

    private static void AssertAdded(bool added)
    {
        if (!added)
        {
            throw new InvalidOperationException("The test witness reference already exists.");
        }
    }
}
