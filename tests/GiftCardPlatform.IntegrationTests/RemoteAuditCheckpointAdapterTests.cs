using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GiftCardPlatform.Api.Services;
using GiftCardPlatform.Modules.Audit.Contracts;

namespace GiftCardPlatform.IntegrationTests;

public sealed class RemoteAuditCheckpointAdapterTests
{
    private static readonly Uri SignerEndpoint = new("https://custody.example/sign");
    private static readonly Uri WitnessBaseUrl = new("https://custody.example/audit/");

    [Fact]
    public async Task Signer_sends_only_the_digest_and_verifies_the_configured_key_contract()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        using var handler = new DelegateHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(SignerEndpoint, request.RequestUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(
                RemoteAuditCheckpointAdapters.SignatureAlgorithm,
                body.RootElement.GetProperty("algorithm").GetString());
            Assert.Equal("kms/key/audit-1", body.RootElement.GetProperty("keyId").GetString());
            var digest = Convert.FromBase64String(
                body.RootElement.GetProperty("digestSha256Base64").GetString()!);
            var signature = key.SignHash(
                digest,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return JsonResponse(new
            {
                algorithm = RemoteAuditCheckpointAdapters.SignatureAlgorithm,
                keyId = "kms/key/audit-1",
                publicKeySpkiBase64 = Convert.ToBase64String(publicKey),
                signatureP1363Base64 = Convert.ToBase64String(signature),
            });
        });
        using var client = new HttpClient(handler);
        using var adapters = RemoteAuditCheckpointAdapters.CreateForTests(
            client,
            SignerEndpoint,
            "kms/key/audit-1",
            WitnessBaseUrl);
        var digest = SHA256.HashData("checkpoint"u8);

        var result = await adapters.SignDigestAsync(digest, CancellationToken.None);

        Assert.Equal("kms/key/audit-1", result.KeyId);
        Assert.Equal(publicKey, result.PublicKey);
        Assert.True(key.VerifyHash(
            digest,
            result.Signature,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task Signer_refuses_a_response_from_a_different_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var handler = new DelegateHandler(request => Task.FromResult(JsonResponse(new
        {
            algorithm = RemoteAuditCheckpointAdapters.SignatureAlgorithm,
            keyId = "kms/key/substituted",
            publicKeySpkiBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            signatureP1363Base64 = Convert.ToBase64String(new byte[64]),
        })));
        using var client = new HttpClient(handler);
        using var adapters = RemoteAuditCheckpointAdapters.CreateForTests(
            client,
            SignerEndpoint,
            "kms/key/audit-1",
            WitnessBaseUrl);

        var exception = await Assert.ThrowsAsync<CryptographicException>(() =>
            adapters.SignDigestAsync(
                SHA256.HashData("checkpoint"u8),
                CancellationToken.None));

        Assert.Contains("unexpected algorithm or key identifier", exception.Message);
    }

    [Fact]
    public async Task Witness_publication_is_create_only_idempotent_and_inventory_visible()
    {
        var stored = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var handler = new DelegateHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put)
            {
                Assert.Contains(System.Net.Http.Headers.EntityTagHeaderValue.Any, request.Headers.IfNoneMatch);
                Assert.True(request.Headers.Contains("Idempotency-Key"));
                Assert.Equal(
                    RemoteAuditCheckpointAdapters.ManifestMediaType,
                    request.Content!.Headers.ContentType!.MediaType);
                Assert.True(request.Content.Headers.Contains("Content-Digest"));
                var bytes = await request.Content.ReadAsByteArrayAsync();
                if (!stored.TryAdd(path, bytes))
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Headers = { Date = DateTimeOffset.Parse("2026-08-25T08:00:00Z", CultureInfo.InvariantCulture) },
                };
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/manifests/", StringComparison.Ordinal))
            {
                return JsonResponse(new
                {
                    references = stored.Keys
                        .Select(item => item[(item.LastIndexOf('/') + 1)..])
                        .ToArray(),
                });
            }

            if (request.Method == HttpMethod.Get && stored.TryGetValue(path, out var existing))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(existing),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);
        using var adapters = RemoteAuditCheckpointAdapters.CreateForTests(
            client,
            SignerEndpoint,
            "kms/key/audit-1",
            WitnessBaseUrl);
        var checkpointId = Guid.Parse("16bb641d-3c7f-4528-9337-a48908bc96ce");
        var manifest = Encoding.UTF8.GetBytes("{\"checkpoint\":\"one\"}");

        var first = await adapters.PublishAsync(
            checkpointId,
            manifest,
            CancellationToken.None);
        var retry = await adapters.PublishAsync(
            checkpointId,
            manifest,
            CancellationToken.None);
        var read = await adapters.ReadAsync(first.Reference, CancellationToken.None);
        var inventory = await adapters.ListReferencesAsync(CancellationToken.None);

        Assert.Equal("16bb641d3c7f45289337a48908bc96ce.json", first.Reference);
        Assert.Equal(first.Reference, retry.Reference);
        Assert.Equal(manifest, read);
        Assert.Equal([first.Reference], inventory);
    }

    [Fact]
    public async Task Witness_refuses_different_bytes_for_an_existing_checkpoint()
    {
        byte[]? stored = null;
        using var handler = new DelegateHandler(async request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync();
                if (stored is null)
                {
                    stored = bytes;
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }

                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(stored!),
            };
        });
        using var client = new HttpClient(handler);
        using var adapters = RemoteAuditCheckpointAdapters.CreateForTests(
            client,
            SignerEndpoint,
            "kms/key/audit-1",
            WitnessBaseUrl);
        var checkpointId = Guid.NewGuid();
        await adapters.PublishAsync(
            checkpointId,
            "first"u8.ToArray(),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapters.PublishAsync(
                checkpointId,
                "second"u8.ToArray(),
                CancellationToken.None));

        Assert.Contains("different bytes", exception.Message);
    }

    [Fact]
    public void Production_factory_refuses_plain_http_before_loading_a_certificate()
    {
        var options = new AuditCheckpointOptions
        {
            RemoteSignerEndpoint = "http://custody.example/sign",
            RemoteSignerKeyId = "kms/key/audit-1",
            RemoteWitnessBaseUrl = "https://custody.example/audit/",
            RemoteClientCertificatePath = "missing.pfx",
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RemoteAuditCheckpointAdapters.Create(options, TimeProvider.System));

        Assert.Contains("absolute HTTPS URL", exception.Message);
    }

    [Fact]
    public void Client_certificate_key_storage_never_uses_an_ephemeral_key_set_on_windows()
    {
        var flags = RemoteAuditCheckpointAdapters.ResolvePkcs12StorageFlags();

        if (OperatingSystem.IsWindows())
        {
            // Schannel refuses an ephemeral private key when presenting a client
            // certificate, and PersistKeySet would leave the key on disk forever.
            Assert.False(flags.HasFlag(X509KeyStorageFlags.EphemeralKeySet));
            Assert.False(flags.HasFlag(X509KeyStorageFlags.PersistKeySet));
        }
        else
        {
            Assert.Equal(X509KeyStorageFlags.EphemeralKeySet, flags);
        }
    }

    [Fact]
    public void Production_factory_loads_a_pfx_whose_private_key_this_platform_can_present()
    {
        var path = WriteTemporaryPfx(TimeSpan.FromDays(-1), TimeSpan.FromDays(1));
        try
        {
            using var adapters = RemoteAuditCheckpointAdapters.Create(
                CertificateOptions(path),
                TimeProvider.System);

            Assert.NotNull(adapters);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Production_factory_refuses_an_expired_client_certificate()
    {
        var path = WriteTemporaryPfx(TimeSpan.FromDays(-10), TimeSpan.FromDays(-1));
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                RemoteAuditCheckpointAdapters.Create(CertificateOptions(path), TimeProvider.System));

            Assert.Contains("outside its validity period", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Production_factory_requires_exactly_one_certificate_source()
    {
        var options = CertificateOptions("present.pfx");
        options.RemoteClientCertificateThumbprint = "AA BB CC";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RemoteAuditCheckpointAdapters.Create(options, TimeProvider.System));

        Assert.Contains("exactly one", exception.Message);
    }

    private const string CertificatePassword = "audit-custody-test";

    private static AuditCheckpointOptions CertificateOptions(string certificatePath) =>
        new()
        {
            RemoteSignerEndpoint = "https://custody.example/sign",
            RemoteSignerKeyId = "kms/key/audit-1",
            RemoteWitnessBaseUrl = "https://custody.example/audit/",
            RemoteClientCertificatePath = certificatePath,
            RemoteClientCertificatePassword = CertificatePassword,
        };

    private static string WriteTemporaryPfx(TimeSpan notBefore, TimeSpan notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=open-giftcard-audit-custody-test",
            key,
            HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now + notBefore, now + notAfter);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, CertificatePassword));
        return path;
    }

    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, ResponseJsonOptions),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return handle(request);
        }
    }
}
