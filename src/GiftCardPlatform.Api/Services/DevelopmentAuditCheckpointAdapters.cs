using System.Security.Cryptography;
using GiftCardPlatform.Modules.Audit.Contracts;

namespace GiftCardPlatform.Api.Services;

internal sealed class DevelopmentFileAuditCheckpointSigner : IAuditCheckpointSigner, IDisposable
{
    private const string Algorithm = "ECDSA-P256-SHA256-P1363";
    private readonly ECDsa key;
    private readonly byte[] publicKey;
    private readonly string keyId;

    public DevelopmentFileAuditCheckpointSigner(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var pem = File.ReadAllText(fullPath);
        key = ECDsa.Create();
        key.ImportFromPem(pem);
        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new CryptographicException("The Development checkpoint key must use ECDSA P-256.");
        }

        publicKey = key.ExportSubjectPublicKeyInfo();
        keyId = $"development-file:{Convert.ToHexString(SHA256.HashData(publicKey))}";
    }

    public Task<AuditCheckpointSignature> SignDigestAsync(
        ReadOnlyMemory<byte> digest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (digest.Length != 32)
        {
            throw new CryptographicException("Only SHA-256 checkpoint digests are accepted.");
        }

        var signature = key.SignHash(
            digest.Span,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Task.FromResult(new AuditCheckpointSignature(
            Algorithm,
            keyId,
            publicKey.ToArray(),
            signature));
    }

    public void Dispose() => key.Dispose();
}

internal sealed class DevelopmentFileAuditCheckpointWitness(
    string directory,
    TimeProvider timeProvider) : IAuditCheckpointWitness
{
    private readonly string root = ResolveRoot(directory);

    public async Task<AuditCheckpointWitnessReceipt> PublishAsync(
        Guid checkpointId,
        ReadOnlyMemory<byte> signedManifest,
        CancellationToken cancellationToken)
    {
        var reference = $"{checkpointId:N}.json";
        var path = ResolveReference(reference);
        Directory.CreateDirectory(root);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(signedManifest, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(existing, signedManifest.Span))
            {
                throw new InvalidOperationException(
                    "The Development witness already contains different bytes for this checkpoint.");
            }
        }

        return new AuditCheckpointWitnessReceipt(reference, timeProvider.GetUtcNow());
    }

    public Task<byte[]?> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        var path = ResolveReference(reference);
        return File.Exists(path)
            ? ReadExistingAsync(path, cancellationToken)
            : Task.FromResult<byte[]?>(null);
    }

    public Task<IReadOnlyCollection<string>> ListReferencesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyCollection<string> references = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        return Task.FromResult(references);
    }

    private static string ResolveRoot(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Path.GetFullPath(directory);
    }

    private string ResolveReference(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (!string.Equals(Path.GetFileName(reference), reference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The witness reference is not a file name.");
        }

        var path = Path.GetFullPath(Path.Combine(root, reference));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The witness reference escapes its configured directory.");
        }

        return path;
    }

    private static async Task<byte[]?> ReadExistingAsync(
        string path,
        CancellationToken cancellationToken) =>
        await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
}
