using System.Globalization;
using System.Reflection;

namespace GiftCardPlatform.ArchitectureTests;

/// <summary>
/// A token codec parses an opaque credential into an identifier and a secret.
/// The identifier is what establishes the transaction-local RLS candidate
/// (`app.share_id`, `app.claim_invitation_id`, and the Payments equivalent), so
/// the TryParse convention is a security boundary here rather than a style
/// preference: a caller that ignored the return value must never be handed an
/// attacker-chosen identifier.
///
/// The invariant held in three separate files only because each got it right
/// independently, and two of them originally did not (IMPL-025). Codecs are
/// therefore discovered over the built module assemblies — any `*Codec` that
/// declares a static `TryParse` — so one added later is covered without anyone
/// remembering to extend this test. A codec with no `TryParse` at all, such as
/// `PosCredentialCodec`, has no identifier to leak and is correctly skipped.
/// </summary>
public sealed class TokenCodecTests
{
    /// <summary>
    /// Every value here must be refused. The last two matter most: a
    /// well-formed identifier whose secret is merely the wrong length takes a
    /// different code path from one that fails to decode at all, and it was the
    /// length path that leaked the identifier.
    /// </summary>
    private static readonly string?[] MalformedTokens =
        [
            null,
            "",
            "   ",
            "not-a-token",
            "0123456789abcdef0123456789abcdef",       // identifier, no separator
            "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.AAAA",  // identifier is not hex
            "0123.0123456789abcdef",                  // separator in the wrong place
            "0123456789abcdef0123456789abcdef.!!!!",  // secret does not decode
            "0123456789abcdef0123456789abcdef.",      // secret decodes to nothing
            "0123456789abcdef0123456789abcdef.AAAA",  // secret decodes, three bytes
            "0123456789abcdef0123456789abcdef." +
                new string('A', 44),                  // secret decodes, 33 bytes
        ];

    /// <summary>
    /// Named so that removing or renaming a codec fails here loudly rather than
    /// quietly shrinking what the behavioural test below covers.
    /// </summary>
    private static readonly string[] KnownCodecs =
        [
            "GiftCardPlatform.Modules.Distribution.Domain.ClaimTokenCodec",
            "GiftCardPlatform.Modules.Payments.Domain.PaymentTokenCodec",
            "GiftCardPlatform.Modules.Sharing.Domain.ShareTokenCodec",
        ];

    private static List<MethodInfo> TokenCodecParsers()
    {
        var parsers = new List<MethodInfo>();

        var moduleAssemblies = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "GiftCardPlatform.Modules.*.dll")
            .Select(Assembly.LoadFrom);

        foreach (var assembly in moduleAssemblies)
        {
            var parseMethods = assembly
                .GetTypes()
                .Where(type => type.Name.EndsWith("Codec", StringComparison.Ordinal))
                .Select(type => type.GetMethod(
                    "TryParse",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(method => method is not null);

            parsers.AddRange(parseMethods!);
        }

        return parsers;
    }

    [Fact]
    public void EveryTokenCodecExposesTheExpectedTryParseShape()
    {
        var parsers = TokenCodecParsers();

        Assert.Superset(
            KnownCodecs.ToHashSet(StringComparer.Ordinal),
            parsers.Select(p => p.DeclaringType!.FullName!).ToHashSet(StringComparer.Ordinal));

        // A codec with a different shape is not safe to skip silently: it would
        // drop out of the behavioural test below without anyone noticing.
        var violations = parsers
            .Where(parser =>
            {
                var parameters = parser.GetParameters();
                return parser.ReturnType != typeof(bool) ||
                    parameters.Length != 3 ||
                    parameters[0].ParameterType != typeof(string) ||
                    parameters[1].ParameterType != typeof(Guid).MakeByRefType() ||
                    parameters[2].ParameterType != typeof(byte[]).MakeByRefType();
            })
            .Select(parser =>
                $"{parser.DeclaringType!.FullName}.TryParse does not match " +
                "bool TryParse(string?, out Guid, out byte[]); extend TokenCodecTests to cover it.");

        Assert.Empty(violations);
    }

    [Fact]
    public void EveryTokenCodecClearsBothOutputsOnEveryParseFailure()
    {
        var violations = new List<string>();

        foreach (var parser in TokenCodecParsers())
        {
            foreach (var token in MalformedTokens)
            {
                object?[] arguments = [token, null, null];

                var parsed = (bool)parser.Invoke(null, arguments)!;
                var identifier = arguments[1];
                var secret = arguments[2] as byte[];

                var subject = $"{parser.DeclaringType!.Name}.TryParse(\"{token ?? "<null>"}\")";

                if (parsed)
                {
                    violations.Add($"{subject} accepted a malformed token.");
                    continue;
                }

                if (!Guid.Empty.Equals(identifier))
                {
                    violations.Add(
                        $"{subject} returned false but left the identifier as {identifier}; " +
                        "a failed parse must not supply an RLS candidate.");
                }

                if (secret is not { Length: 0 })
                {
                    violations.Add(
                        $"{subject} returned false but left a " +
                        $"{secret?.Length.ToString(CultureInfo.InvariantCulture) ?? "null"}-byte secret.");
                }
            }
        }

        Assert.Empty(violations);
    }
}
