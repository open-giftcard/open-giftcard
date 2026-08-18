using System.Security.Cryptography;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Audit.Domain;
using GiftCardPlatform.Modules.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Audit.Application;

internal sealed class AuditCheckpointProcessor(
    AuditDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IAuditCheckpointSigner signer,
    IAuditCheckpointWitness witness,
    TimeProvider timeProvider) : IAuditCheckpointProcessor
{
    public async Task<AuditCheckpointPassResult> ProcessNextAsync(
        int maximumRecords,
        CancellationToken cancellationToken)
    {
        if (maximumRecords is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        }

        if (await SignPendingAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AuditCheckpointPassResult(false, true, false);
        }

        if (await WitnessPendingAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AuditCheckpointPassResult(false, false, true);
        }

        if (await HasPendingAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AuditCheckpointPassResult(false, false, false);
        }

        var manifestCreated = await CreateManifestAsync(maximumRecords, cancellationToken)
            .ConfigureAwait(false);
        return new AuditCheckpointPassResult(manifestCreated, false, false);
    }

    public async Task<AuditCheckpointVerificationResult> VerifyAsync(
        CancellationToken cancellationToken)
    {
        List<AuditCheckpoint> checkpoints;
        List<AuditCheckpointSeal> seals;
        List<AuditCheckpointWitness> witnesses;
        List<AuditRecord> records;

        await using (var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            checkpoints = await dbContext.AuditCheckpoints
                .AsNoTracking()
                .OrderBy(item => item.FirstSequence)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            seals = await dbContext.AuditCheckpointSeals
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            witnesses = await dbContext.AuditCheckpointWitnesses
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            records = await dbContext.AuditRecords
                .IgnoreQueryFilters()
                .AsNoTracking()
                .OrderBy(item => item.Sequence)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var sealById = seals.ToDictionary(item => item.CheckpointId);
        var witnessById = witnesses.ToDictionary(item => item.CheckpointId);
        var externalReferences = await witness.ListReferencesAsync(cancellationToken)
            .ConfigureAwait(false);
        var receiptReferences = witnesses
            .Select(item => item.Reference)
            .ToHashSet(StringComparer.Ordinal);
        if (externalReferences.Count != receiptReferences.Count ||
            externalReferences.Any(reference => !receiptReferences.Contains(reference)))
        {
            return Failure(checkpoints, seals, witnesses, "checkpoint_witness_inventory_invalid");
        }

        AuditCheckpoint? previous = null;

        foreach (var checkpoint in checkpoints)
        {
            if (!ChainMatches(previous, checkpoint))
            {
                return Failure(checkpoints, seals, witnesses, "checkpoint_chain_invalid");
            }

            var batch = records
                .Where(item => item.Sequence >= checkpoint.FirstSequence &&
                    item.Sequence <= checkpoint.LastSequence)
                .ToList();
            if (batch.Count != checkpoint.RecordCount ||
                batch.Count == 0 ||
                batch[0].Sequence != checkpoint.FirstSequence ||
                batch[^1].Sequence != checkpoint.LastSequence ||
                !CryptographicOperations.FixedTimeEquals(
                    AuditCheckpointCryptography.ComputeMerkleRoot(batch),
                    checkpoint.MerkleRoot))
            {
                return Failure(checkpoints, seals, witnesses, "checkpoint_records_invalid");
            }

            var expectedDigest = AuditCheckpointCryptography.ComputeManifestDigest(
                checkpoint.Id,
                checkpoint.PreviousCheckpointId,
                checkpoint.PreviousManifestDigest,
                checkpoint.FirstSequence,
                checkpoint.LastSequence,
                checkpoint.RecordCount,
                checkpoint.MerkleRoot,
                checkpoint.CreatedAtUtc);
            if (!CryptographicOperations.FixedTimeEquals(expectedDigest, checkpoint.ManifestDigest))
            {
                return Failure(checkpoints, seals, witnesses, "checkpoint_manifest_invalid");
            }

            if (!sealById.TryGetValue(checkpoint.Id, out var seal) ||
                !AuditCheckpointCryptography.VerifySignature(checkpoint, seal))
            {
                return Failure(checkpoints, seals, witnesses, "checkpoint_signature_invalid");
            }

            if (!witnessById.TryGetValue(checkpoint.Id, out var receipt) ||
                !CryptographicOperations.FixedTimeEquals(
                    receipt.ManifestDigest,
                    checkpoint.ManifestDigest))
            {
                return Failure(checkpoints, seals, witnesses, "checkpoint_witness_missing");
            }

            var witnessedBytes = await witness.ReadAsync(receipt.Reference, cancellationToken)
                .ConfigureAwait(false);
            var expectedBytes = AuditCheckpointCryptography.BuildSignedManifest(checkpoint, seal);
            if (witnessedBytes is null ||
                !CryptographicOperations.FixedTimeEquals(witnessedBytes, expectedBytes))
            {
                return Failure(checkpoints, seals, witnesses, "checkpoint_witness_invalid");
            }

            previous = checkpoint;
        }

        if (checkpoints.Count != seals.Count || checkpoints.Count != witnesses.Count)
        {
            return Failure(checkpoints, seals, witnesses, "checkpoint_orphan_evidence");
        }

        return new AuditCheckpointVerificationResult(
            true,
            checkpoints.Count,
            seals.Count,
            witnesses.Count,
            null);
    }

    private async Task<bool> CreateManifestAsync(
        int maximumRecords,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AuditCheckpointLock.AcquireSealerAsync(dbContext, cancellationToken)
            .ConfigureAwait(false);

        if (await dbContext.AuditCheckpoints.AnyAsync(
                item => !dbContext.AuditCheckpointSeals.Any(seal => seal.CheckpointId == item.Id) ||
                    !dbContext.AuditCheckpointWitnesses.Any(receipt => receipt.CheckpointId == item.Id),
                cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var previous = await dbContext.AuditCheckpoints
            .AsNoTracking()
            .OrderByDescending(item => item.LastSequence)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var lastSequence = previous?.LastSequence ?? 0;
        var records = await dbContext.AuditRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Sequence > lastSequence)
            .OrderBy(item => item.Sequence)
            .Take(maximumRecords)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (records.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var checkpoint = AuditCheckpoint.Create(
            previous?.Id,
            previous?.ManifestDigest,
            records[0].Sequence,
            records[^1].Sequence,
            records.Count,
            AuditCheckpointCryptography.ComputeMerkleRoot(records),
            timeProvider.GetUtcNow());
        dbContext.AuditCheckpoints.Add(checkpoint);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SignPendingAsync(CancellationToken cancellationToken)
    {
        var checkpoint = await dbContext.AuditCheckpoints
            .AsNoTracking()
            .Where(item => !dbContext.AuditCheckpointSeals.Any(seal => seal.CheckpointId == item.Id))
            .OrderBy(item => item.FirstSequence)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (checkpoint is null)
        {
            return false;
        }

        var signed = await signer
            .SignDigestAsync(checkpoint.ManifestDigest, cancellationToken)
            .ConfigureAwait(false);
        ValidateSignatureContract(signed);
        var seal = new AuditCheckpointSeal(
            checkpoint.Id,
            signed.Algorithm,
            signed.KeyId,
            signed.PublicKey,
            signed.Signature,
            timeProvider.GetUtcNow());
        if (!AuditCheckpointCryptography.VerifySignature(checkpoint, seal))
        {
            throw new CryptographicException("The checkpoint signer returned an invalid signature.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        dbContext.AuditCheckpointSeals.Add(seal);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> WitnessPendingAsync(CancellationToken cancellationToken)
    {
        var candidate = await (
                from checkpoint in dbContext.AuditCheckpoints.AsNoTracking()
                join seal in dbContext.AuditCheckpointSeals.AsNoTracking()
                    on checkpoint.Id equals seal.CheckpointId
                where !dbContext.AuditCheckpointWitnesses.Any(
                    receipt => receipt.CheckpointId == checkpoint.Id)
                orderby checkpoint.FirstSequence
                select new { Checkpoint = checkpoint, Seal = seal })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return false;
        }

        var manifest = AuditCheckpointCryptography.BuildSignedManifest(
            candidate.Checkpoint,
            candidate.Seal);
        var published = await witness.PublishAsync(
                candidate.Checkpoint.Id,
                manifest,
                cancellationToken)
            .ConfigureAwait(false);

        ArgumentException.ThrowIfNullOrWhiteSpace(published.Reference);
        var receipt = new AuditCheckpointWitness(
            candidate.Checkpoint.Id,
            published.Reference,
            candidate.Checkpoint.ManifestDigest,
            published.PublishedAtUtc);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        dbContext.AuditCheckpointWitnesses.Add(receipt);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task<bool> HasPendingAsync(CancellationToken cancellationToken) =>
        dbContext.AuditCheckpoints.AnyAsync(
            item => !dbContext.AuditCheckpointSeals.Any(seal => seal.CheckpointId == item.Id) ||
                !dbContext.AuditCheckpointWitnesses.Any(receipt => receipt.CheckpointId == item.Id),
            cancellationToken);

    private static bool ChainMatches(AuditCheckpoint? previous, AuditCheckpoint current)
    {
        if (previous is null)
        {
            return current.PreviousCheckpointId is null &&
                current.PreviousManifestDigest is null;
        }

        return current.PreviousCheckpointId == previous.Id &&
            current.FirstSequence > previous.LastSequence &&
            current.PreviousManifestDigest is not null &&
            CryptographicOperations.FixedTimeEquals(
                current.PreviousManifestDigest,
                previous.ManifestDigest);
    }

    private static void ValidateSignatureContract(AuditCheckpointSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (!string.Equals(
                signature.Algorithm,
                AuditCheckpointCryptography.SignatureAlgorithm,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(signature.KeyId) ||
            signature.KeyId.Length > 512 ||
            signature.PublicKey.Length == 0 ||
            signature.Signature.Length != 64)
        {
            throw new CryptographicException("The checkpoint signer returned an unsupported result.");
        }
    }

    private static AuditCheckpointVerificationResult Failure(
        List<AuditCheckpoint> checkpoints,
        List<AuditCheckpointSeal> seals,
        List<AuditCheckpointWitness> witnesses,
        string code) =>
        new(false, checkpoints.Count, seals.Count, witnesses.Count, code);
}
