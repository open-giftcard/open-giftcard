using System.Security.Cryptography;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal static class GiftCardPublicReferenceGenerator
{
    public static string Create() =>
        "GC-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
}
