using System.Runtime.CompilerServices;

// Domain types stay internal to preserve the module boundary; the test projects
// are granted access so invariants can be tested directly rather than by
// widening the module's public surface.
[assembly: InternalsVisibleTo("GiftCardPlatform.UnitTests")]
[assembly: InternalsVisibleTo("GiftCardPlatform.IntegrationTests")]
