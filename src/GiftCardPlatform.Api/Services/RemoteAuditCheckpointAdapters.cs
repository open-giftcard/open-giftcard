using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GiftCardPlatform.Modules.Audit.Contracts;

namespace GiftCardPlatform.Api.Services;

/// <summary>
/// Production transport for a separately operated audit-custody gateway. The
/// gateway keeps the checkpoint signing key in KMS/HSM custody and publishes
/// signed manifests into immutable storage. Mutual TLS authenticates this host
/// without giving it the checkpoint private key.
/// </summary>
internal sealed class RemoteAuditCheckpointAdapters :
    IAuditCheckpointSigner,
    IAuditCheckpointWitness,
    IDisposable
{
    internal const string SignatureAlgorithm = "ECDSA-P256-SHA256-P1363";
    private const string NistP256Oid = "1.2.840.10045.3.1.7";
    internal const string ManifestMediaType =
        "application/vnd.open-giftcard.audit-checkpoint+json";
    private const int MaximumSignerResponseBytes = 64 * 1024;
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumInventoryResponseBytes = 8 * 1024 * 1024;
    private const int MaximumInventoryReferences = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly Uri signerEndpoint;
    private readonly Uri witnessManifestsEndpoint;
    private readonly string signerKeyId;
    private readonly TimeProvider timeProvider;
    private readonly bool ownsHttpClient;
    private readonly X509Certificate2? clientCertificate;

    private RemoteAuditCheckpointAdapters(
        HttpClient httpClient,
        Uri signerEndpoint,
        string signerKeyId,
        Uri witnessManifestsEndpoint,
        TimeProvider timeProvider,
        bool ownsHttpClient,
        X509Certificate2? clientCertificate)
    {
        this.httpClient = httpClient;
        this.signerEndpoint = signerEndpoint;
        this.signerKeyId = signerKeyId;
        this.witnessManifestsEndpoint = witnessManifestsEndpoint;
        this.timeProvider = timeProvider;
        this.ownsHttpClient = ownsHttpClient;
        this.clientCertificate = clientCertificate;
    }

    public static RemoteAuditCheckpointAdapters Create(
        AuditCheckpointOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var signerEndpoint = RequireHttpsEndpoint(
            options.RemoteSignerEndpoint,
            nameof(options.RemoteSignerEndpoint),
            requireTrailingSlash: false);
        var witnessBaseUrl = RequireHttpsEndpoint(
            options.RemoteWitnessBaseUrl,
            nameof(options.RemoteWitnessBaseUrl),
            requireTrailingSlash: true);
        var signerKeyId = options.RemoteSignerKeyId;
        if (string.IsNullOrWhiteSpace(signerKeyId) || signerKeyId.Length > 512)
        {
            throw new InvalidOperationException(
                "Audit:Checkpoints:RemoteSignerKeyId is required and cannot exceed 512 characters.");
        }

        var certificate = LoadClientCertificate(options, timeProvider.GetUtcNow());
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CheckCertificateRevocationList = true,
            ClientCertificateOptions = ClientCertificateOption.Manual,
        };
        handler.ClientCertificates.Add(certificate);

        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(options.RemoteTimeoutSeconds),
        };
        return new RemoteAuditCheckpointAdapters(
            httpClient,
            signerEndpoint,
            signerKeyId,
            new Uri(witnessBaseUrl, "manifests/"),
            timeProvider,
            ownsHttpClient: true,
            clientCertificate: certificate);
    }

    internal static RemoteAuditCheckpointAdapters CreateForTests(
        HttpClient httpClient,
        Uri signerEndpoint,
        string signerKeyId,
        Uri witnessBaseUrl,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(signerKeyId);
        return new RemoteAuditCheckpointAdapters(
            httpClient,
            signerEndpoint,
            signerKeyId,
            new Uri(witnessBaseUrl, "manifests/"),
            timeProvider ?? TimeProvider.System,
            ownsHttpClient: false,
            clientCertificate: null);
    }

    public async Task<AuditCheckpointSignature> SignDigestAsync(
        ReadOnlyMemory<byte> digest,
        CancellationToken cancellationToken)
    {
        if (digest.Length != 32)
        {
            throw new CryptographicException("Only SHA-256 checkpoint digests are accepted.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, signerEndpoint)
        {
            Content = JsonContent.Create(
                new RemoteSignRequest(
                    SignatureAlgorithm,
                    signerKeyId,
                    Convert.ToBase64String(digest.Span)),
                options: JsonOptions),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, "signer");
        var bytes = await ReadBoundedAsync(
                response.Content,
                MaximumSignerResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        RemoteSignResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<RemoteSignResponse>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            // The parser message quotes the offending bytes, so it is dropped
            // rather than chained as an inner exception.
            throw new CryptographicException("The checkpoint signer returned malformed JSON.");
        }

        if (result is null)
        {
            throw new CryptographicException("The checkpoint signer returned an empty result.");
        }
        if (!string.Equals(result.Algorithm, SignatureAlgorithm, StringComparison.Ordinal) ||
            !string.Equals(result.KeyId, signerKeyId, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                "The checkpoint signer returned an unexpected algorithm or key identifier.");
        }

        var publicKey = DecodeBase64(result.PublicKeySpkiBase64, "public key");
        var signature = DecodeBase64(result.SignatureP1363Base64, "signature");
        ValidatePublicKey(publicKey);
        if (signature.Length != 64)
        {
            throw new CryptographicException(
                "The checkpoint signer did not return a 64-byte P1363 signature.");
        }

        return new AuditCheckpointSignature(
            result.Algorithm,
            result.KeyId,
            publicKey,
            signature);
    }

    public async Task<AuditCheckpointWitnessReceipt> PublishAsync(
        Guid checkpointId,
        ReadOnlyMemory<byte> signedManifest,
        CancellationToken cancellationToken)
    {
        if (signedManifest.IsEmpty || signedManifest.Length > MaximumManifestBytes)
        {
            throw new InvalidOperationException("The signed checkpoint manifest size is invalid.");
        }

        var reference = BuildReference(checkpointId);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(witnessManifestsEndpoint, reference));
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", checkpointId.ToString("N"));
        request.Content = new ReadOnlyMemoryContent(signedManifest);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ManifestMediaType);
        request.Content.Headers.TryAddWithoutValidation(
            "Content-Digest",
            $"sha-256=:{Convert.ToBase64String(SHA256.HashData(signedManifest.Span))}:");

        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            var existing = await ReadRequiredAsync(reference, cancellationToken)
                .ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(existing, signedManifest.Span))
            {
                throw new InvalidOperationException(
                    "The immutable witness already contains different bytes for this checkpoint.");
            }

            return new AuditCheckpointWitnessReceipt(reference, ResolvePublishedAt(response));
        }

        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.Created)
        {
            throw CreateHttpFailure(response, "witness publication");
        }

        return new AuditCheckpointWitnessReceipt(reference, ResolvePublishedAt(response));
    }

    public async Task<byte[]?> ReadAsync(
        string reference,
        CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        using var response = await httpClient.GetAsync(
                new Uri(witnessManifestsEndpoint, reference),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, "witness read");
        return await ReadBoundedAsync(
                response.Content,
                MaximumManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<string>> ListReferencesAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, witnessManifestsEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, "witness inventory");
        var bytes = await ReadBoundedAsync(
                response.Content,
                MaximumInventoryResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        WitnessInventoryResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<WitnessInventoryResponse>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The witness inventory returned malformed JSON.");
        }

        if (result is null)
        {
            throw new InvalidOperationException("The witness inventory returned an empty result.");
        }
        if (result.References is null || result.References.Count > MaximumInventoryReferences)
        {
            throw new InvalidOperationException("The witness inventory size is invalid.");
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in result.References)
        {
            ValidateReference(reference);
            if (!unique.Add(reference))
            {
                throw new InvalidOperationException(
                    "The witness inventory contains a duplicate reference.");
            }
        }

        return unique.Order(StringComparer.Ordinal).ToArray();
    }

    public void Dispose()
    {
        if (!ownsHttpClient)
        {
            return;
        }

        // The handler holds the certificate for the lifetime of the connection
        // pool, so the client is torn down first. Disposing the certificate
        // afterwards releases the temporary Windows key container opened by
        // ResolvePkcs12StorageFlags.
        httpClient.Dispose();
        clientCertificate?.Dispose();
    }

    private async Task<byte[]> ReadRequiredAsync(
        string reference,
        CancellationToken cancellationToken) =>
        await ReadAsync(reference, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The immutable witness refused publication but the object cannot be read.");

    private DateTimeOffset ResolvePublishedAt(HttpResponseMessage response) =>
        response.Content.Headers.LastModified ??
        response.Headers.Date ??
        timeProvider.GetUtcNow();

    private static Uri RequireHttpsEndpoint(
        string? value,
        string optionName,
        bool requireTrailingSlash)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                $"Audit:Checkpoints:{optionName} must be an absolute HTTPS URL without credentials, query, or fragment.");
        }

        if (!requireTrailingSlash)
        {
            return endpoint;
        }

        var builder = new UriBuilder(endpoint);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    /// <summary>
    /// Windows Schannel cannot present a client certificate whose private key
    /// lives in an ephemeral key set: mutual TLS fails at handshake time with
    /// SEC_E_UNKNOWN_CREDENTIALS. Windows therefore gets a key set backed by a
    /// container, deliberately without <see cref="X509KeyStorageFlags.PersistKeySet"/>
    /// so the container is temporary and removed when the certificate is
    /// disposed. Every other platform keeps the key in memory only.
    /// </summary>
    internal static X509KeyStorageFlags ResolvePkcs12StorageFlags() =>
        OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.UserKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

    private static X509Certificate2 LoadClientCertificate(
        AuditCheckpointOptions options,
        DateTimeOffset now)
    {
        var hasPath = !string.IsNullOrWhiteSpace(options.RemoteClientCertificatePath);
        var hasThumbprint = !string.IsNullOrWhiteSpace(
            options.RemoteClientCertificateThumbprint);
        if (hasPath == hasThumbprint)
        {
            throw new InvalidOperationException(
                "Configure exactly one of Audit:Checkpoints:RemoteClientCertificatePath " +
                "or RemoteClientCertificateThumbprint.");
        }

        X509Certificate2 certificate;
        if (hasPath)
        {
            var fullPath = Path.GetFullPath(options.RemoteClientCertificatePath!);
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                fullPath,
                options.RemoteClientCertificatePassword,
                ResolvePkcs12StorageFlags());
        }
        else
        {
            certificate = FindCertificateByThumbprint(
                options.RemoteClientCertificateThumbprint!);
        }

        if (!certificate.HasPrivateKey ||
            now < certificate.NotBefore.ToUniversalTime() ||
            now > certificate.NotAfter.ToUniversalTime())
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                "The audit custody client certificate is missing its private key or is outside its validity period.");
        }

        return certificate;
    }

    private static X509Certificate2 FindCertificateByThumbprint(string thumbprint)
    {
        var normalized = string.Concat(thumbprint.Where(char.IsAsciiHexDigit)).ToUpperInvariant();
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException(
                "Audit:Checkpoints:RemoteClientCertificateThumbprint is invalid.");
        }

        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var matches = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                normalized,
                validOnly: true);
            if (matches.Count > 0)
            {
                return matches[0];
            }
        }

        throw new InvalidOperationException(
            "The configured audit custody client certificate was not found in the CurrentUser or LocalMachine personal store.");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure(response, operation);
        }
    }

    private static HttpRequestException CreateHttpFailure(
        HttpResponseMessage response,
        string operation) =>
        new(
            $"The remote audit {operation} failed with HTTP {(int)response.StatusCode}.",
            null,
            response.StatusCode);

    private static byte[] DecodeBase64(string? value, string field)
    {
        try
        {
            return Convert.FromBase64String(value ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException(
                $"The checkpoint signer returned an invalid {field}.",
                exception);
        }
    }

    private static void ValidatePublicKey(byte[] publicKey)
    {
        if (publicKey.Length is 0 or > 1024)
        {
            throw new CryptographicException("The checkpoint signer returned an invalid public key.");
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
        if (bytesRead != publicKey.Length || key.KeySize != 256 || !IsNistP256(key))
        {
            throw new CryptographicException(
                "The checkpoint signer public key is not an ECDSA P-256 key.");
        }
    }

    /// <summary>
    /// A 256-bit key size does not identify the curve: secp256k1 and
    /// brainpoolP256r1 are also 256 bits and would otherwise pass a check that
    /// claims to accept only P-256.
    /// </summary>
    private static bool IsNistP256(ECDsa key)
    {
        var curve = key.ExportParameters(includePrivateParameters: false).Curve;
        if (!curve.IsNamed)
        {
            return false;
        }

        // Oid.Value is not populated on every platform, so the friendly name is
        // accepted as a fallback rather than as the primary identifier.
        return string.Equals(curve.Oid.Value, NistP256Oid, StringComparison.Ordinal) ||
            (string.IsNullOrEmpty(curve.Oid.Value) &&
                (string.Equals(curve.Oid.FriendlyName, "nistP256", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(curve.Oid.FriendlyName, "ECDSA_P256", StringComparison.OrdinalIgnoreCase)));
    }

    private static string BuildReference(Guid checkpointId) =>
        $"{checkpointId:N}.json";

    private static void ValidateReference(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.Length != 37 ||
            !reference.EndsWith(".json", StringComparison.Ordinal) ||
            !Guid.TryParseExact(reference[..32], "N", out var parsed) ||
            !string.Equals(BuildReference(parsed), reference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The witness returned an invalid reference.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidOperationException("The remote audit response exceeded its size limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidOperationException(
                    "The remote audit response exceeded its size limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed record RemoteSignRequest(
        string Algorithm,
        string KeyId,
        string DigestSha256Base64);

    private sealed record RemoteSignResponse(
        string Algorithm,
        string KeyId,
        string PublicKeySpkiBase64,
        string SignatureP1363Base64);

    private sealed record WitnessInventoryResponse(IReadOnlyList<string>? References);
}
