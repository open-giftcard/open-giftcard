namespace GiftCardPlatform.Api;

/// <summary>
/// Route prefixes for the public HTTP API (ADR-027).
///
/// The version lives in the URL so a POS terminal or mobile client pinned to an
/// older contract keeps working while a newer one ships alongside it. Adding a
/// breaking change means introducing the next prefix and serving both, never
/// altering an existing one in place.
/// </summary>
internal static class ApiRoutes
{
    public const string V1 = "/api/v1";
}
