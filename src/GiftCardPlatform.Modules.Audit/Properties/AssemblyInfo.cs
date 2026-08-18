using System.Runtime.CompilerServices;

// The integration tests verify append-only audit behaviour and atomic commit,
// which requires reading the module's internal DbContext directly.
[assembly: InternalsVisibleTo("GiftCardPlatform.IntegrationTests")]
[assembly: InternalsVisibleTo("GiftCardPlatform.UnitTests")]
