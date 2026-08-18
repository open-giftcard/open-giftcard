using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GiftCardPlatform.Modules.Audit.Domain;

internal static class AuditCheckpointCryptography
{
    public const string SignatureAlgorithm = "ECDSA-P256-SHA256-P1363";

    private static readonly byte[] ManifestDomain =
        Encoding.ASCII.GetBytes("GIFTCARD-AUDIT-CHECKPOINT-V1");

    public static byte[] ComputeMerkleRoot(IReadOnlyList<AuditRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            throw new ArgumentException("A checkpoint requires at least one audit record.", nameof(records));
        }

        var stack = new List<byte[]>();
        for (var index = 0; index < records.Count; index++)
        {
            stack.Add(ComputeLeafHash(records[index]));
            var mergeCount = CountTrailingOnes(index);
            for (var merge = 0; merge < mergeCount; merge++)
            {
                MergeTop(stack);
            }
        }

        while (stack.Count > 1)
        {
            MergeTop(stack);
        }

        return stack[0];
    }

    public static byte[] ComputeLeafHash(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var stream = new MemoryStream();
        stream.WriteByte(0x00); // RFC 9162 leaf domain separation.
        WriteInt32(stream, 1);
        WriteInt64(stream, record.Sequence);
        WriteGuid(stream, record.Id);
        WriteGuid(stream, record.ActorUserId);
        WriteString(stream, record.ActorType.ToString());
        WriteNullableGuid(stream, record.ActorMembershipId);
        WriteNullableGuid(stream, record.OrganizationScopeId);
        WriteString(stream, record.Operation);
        WriteString(stream, record.EntityType);
        WriteString(stream, record.EntityId);
        WriteString(stream, record.Outcome.ToString());
        WriteGuid(stream, record.CorrelationId);
        WriteInt64(stream, record.OccurredAtUtc.UtcTicks);
        WriteNullableString(stream, record.MetadataJson);
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    public static byte[] ComputeManifestDigest(
        Guid id,
        Guid? previousCheckpointId,
        byte[]? previousManifestDigest,
        long firstSequence,
        long lastSequence,
        int recordCount,
        byte[] merkleRoot,
        DateTimeOffset createdAtUtc)
    {
        using var stream = new MemoryStream();
        WriteBytes(stream, ManifestDomain);
        WriteInt32(stream, 1);
        WriteGuid(stream, id);
        WriteNullableGuid(stream, previousCheckpointId);
        WriteNullableBytes(stream, previousManifestDigest);
        WriteInt64(stream, firstSequence);
        WriteInt64(stream, lastSequence);
        WriteInt32(stream, recordCount);
        WriteBytes(stream, merkleRoot);
        WriteInt64(stream, createdAtUtc.UtcTicks);
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    public static bool VerifySignature(AuditCheckpoint checkpoint, AuditCheckpointSeal seal)
    {
        if (!string.Equals(seal.Algorithm, SignatureAlgorithm, StringComparison.Ordinal) ||
            seal.PublicKey.Length == 0 || seal.Signature.Length != 64)
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(seal.PublicKey, out var consumed);
            return consumed == seal.PublicKey.Length &&
                ecdsa.KeySize == 256 &&
                ecdsa.VerifyHash(
                    checkpoint.ManifestDigest,
                    seal.Signature,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static byte[] BuildSignedManifest(AuditCheckpoint checkpoint, AuditCheckpointSeal seal)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", checkpoint.FormatVersion);
            writer.WriteString("checkpointId", checkpoint.Id);
            if (checkpoint.PreviousCheckpointId is { } previousId)
            {
                writer.WriteString("previousCheckpointId", previousId);
            }
            else
            {
                writer.WriteNull("previousCheckpointId");
            }

            WriteBase64OrNull(writer, "previousManifestDigest", checkpoint.PreviousManifestDigest);
            writer.WriteNumber("firstSequence", checkpoint.FirstSequence);
            writer.WriteNumber("lastSequence", checkpoint.LastSequence);
            writer.WriteNumber("recordCount", checkpoint.RecordCount);
            writer.WriteString("hashAlgorithm", checkpoint.HashAlgorithm);
            writer.WriteBase64String("merkleRoot", checkpoint.MerkleRoot);
            writer.WriteBase64String("manifestDigest", checkpoint.ManifestDigest);
            writer.WriteString("createdAtUtc", checkpoint.CreatedAtUtc);
            writer.WriteString("signatureAlgorithm", seal.Algorithm);
            writer.WriteString("signingKeyId", seal.KeyId);
            writer.WriteBase64String("publicKey", seal.PublicKey);
            writer.WriteBase64String("signature", seal.Signature);
            writer.WriteString("signedAtUtc", seal.SignedAtUtc);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static int CountTrailingOnes(int value)
    {
        var count = 0;
        while ((value & 1) == 1)
        {
            count++;
            value >>= 1;
        }

        return count;
    }

    private static void MergeTop(List<byte[]> stack)
    {
        var right = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        var left = stack[^1];
        stack.RemoveAt(stack.Count - 1);

        Span<byte> input = stackalloc byte[65];
        input[0] = 0x01; // RFC 9162 internal-node domain separation.
        left.CopyTo(input[1..33]);
        right.CopyTo(input[33..]);
        stack.Add(SHA256.HashData(input));
    }

    private static void WriteBase64OrNull(Utf8JsonWriter writer, string name, byte[]? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteBase64String(name, value);
        }
    }

    private static void WriteGuid(Stream stream, Guid value) => WriteString(stream, value.ToString("N"));

    private static void WriteNullableGuid(Stream stream, Guid? value)
    {
        stream.WriteByte(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            WriteGuid(stream, value.Value);
        }
    }

    private static void WriteNullableString(Stream stream, string? value)
    {
        stream.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null)
        {
            WriteString(stream, value);
        }
    }

    private static void WriteString(Stream stream, string value) =>
        WriteBytes(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteNullableBytes(Stream stream, byte[]? value)
    {
        stream.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null)
        {
            WriteBytes(stream, value);
        }
    }

    private static void WriteBytes(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteInt32(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
