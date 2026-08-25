using System.Text.Json;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Keeps the served contract honest about what callers must send.
///
/// <para>
/// Until 2026-08-25 not one of the document's object schemas carried a
/// <c>required</c> array, so required-ness was not expressible at all. A client
/// generated from the published document got every field optional, and no
/// client-side contract test could check otherwise. The POS client sent no
/// idempotency key for four days after the backend began demanding one, and
/// every contract assertion on both sides passed throughout, because the only
/// thing they could check was that the field existed.
/// </para>
///
/// <para>
/// Every financial operation is idempotent, keyed by operation type and
/// idempotency key with a database unique constraint, and the domain throws
/// <c>ValidationFailedException</c> when the key is absent. These tests assert
/// the document says so, so a new financial endpoint cannot quietly ship a key
/// that reads as optional to everyone consuming the contract.
/// </para>
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class RequiredFieldContractTests(PlatformApiFixture fixture)
{
    [Fact]
    public async Task Every_request_body_carrying_an_idempotency_key_declares_it_required()
    {
        using var document = await LoadDocumentAsync();
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var offenders = new List<string>();
        var checkedCount = 0;
        foreach (var name in RequestSchemaNames(document.RootElement))
        {
            if (!schemas.TryGetProperty(name, out var schema) ||
                !schema.TryGetProperty("properties", out var properties) ||
                !properties.TryGetProperty("idempotencyKey", out _))
            {
                continue;
            }

            checkedCount++;
            var declared = schema.TryGetProperty("required", out var required) &&
                required.EnumerateArray().Any(item =>
                    string.Equals(item.GetString(), "idempotencyKey", StringComparison.Ordinal));
            if (!declared)
            {
                offenders.Add(name);
            }
        }

        Assert.True(
            checkedCount > 0,
            "No request schema carried an idempotency key, so this test proved nothing. " +
            "Either the document shape changed or it failed to load.");
        Assert.True(
            offenders.Count == 0,
            "These request schemas take an idempotency key the domain refuses to run without, " +
            "yet the contract presents it as optional: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// A field marked required must not also be advertised as nullable. Sending
    /// an explicit null would satisfy a generated client and still be refused by
    /// the domain, which is the same failure this work exists to remove.
    /// </summary>
    [Fact]
    public async Task No_required_field_is_also_declared_nullable()
    {
        using var document = await LoadDocumentAsync();
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var offenders = new List<string>();
        foreach (var schema in schemas.EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("required", out var required) ||
                !schema.Value.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var name in required.EnumerateArray().Select(item => item.GetString()))
            {
                if (properties.TryGetProperty(name!, out var property) &&
                    property.TryGetProperty("nullable", out var nullable) &&
                    nullable.GetBoolean())
                {
                    offenders.Add($"{schema.Name}.{name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Required and nullable at once: " + string.Join(", ", offenders));
    }

    private static IEnumerable<string> RequestSchemaNames(JsonElement root)
    {
        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Value.ValueKind != JsonValueKind.Object ||
                    !operation.Value.TryGetProperty("requestBody", out var body) ||
                    !body.TryGetProperty("content", out var content))
                {
                    continue;
                }

                foreach (var media in content.EnumerateObject())
                {
                    if (media.Value.TryGetProperty("schema", out var schema) &&
                        schema.TryGetProperty("$ref", out var reference))
                    {
                        yield return reference.GetString()!.Split('/')[^1];
                    }
                }
            }
        }
    }

    private async Task<JsonDocument> LoadDocumentAsync()
    {
        var response = await fixture.Factory.CreateClient()
            .GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
