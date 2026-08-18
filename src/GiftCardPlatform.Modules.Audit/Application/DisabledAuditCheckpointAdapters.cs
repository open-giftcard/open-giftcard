using GiftCardPlatform.Modules.Audit.Contracts;

namespace GiftCardPlatform.Modules.Audit.Application;

/// <summary>
/// Fail-closed defaults keep a disabled host constructible under dependency
/// validation. An enabled host must register real adapters after the module;
/// these defaults can never produce checkpoint evidence.
/// </summary>
internal sealed class DisabledAuditCheckpointSigner : IAuditCheckpointSigner
{
    public Task<AuditCheckpointSignature> SignDigestAsync(
        ReadOnlyMemory<byte> digest,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Audit checkpoint signing is disabled because no signer adapter is registered.");
}

internal sealed class DisabledAuditCheckpointWitness : IAuditCheckpointWitness
{
    public Task<AuditCheckpointWitnessReceipt> PublishAsync(
        Guid checkpointId,
        ReadOnlyMemory<byte> signedManifest,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Audit checkpoint witnessing is disabled because no witness adapter is registered.");

    public Task<byte[]?> ReadAsync(
        string reference,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Audit checkpoint verification is disabled because no witness adapter is registered.");

    public Task<IReadOnlyCollection<string>> ListReferencesAsync(
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Audit checkpoint verification is disabled because no witness adapter is registered.");
}
