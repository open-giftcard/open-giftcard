## What this changes

Describe the behaviour before and after. If this fixes something, say what made
it wrong rather than only what the fix does.

## How it was verified

State what you ran and what it said. If you did not run something, say that
instead of leaving it implied.

- [ ] `dotnet build GiftCardPlatform.slnx -c Release`
- [ ] Unit tests
- [ ] Architecture tests
- [ ] Integration tests against real PostgreSQL (`GIFTCARD_TEST_CONNECTION`)
- [ ] Not applicable, because:

## If it touches the database

- [ ] One migration per module, applied by the migration owner role
- [ ] A new tenant table carries its forced row-level-security policy in the
      migration that creates it
- [ ] A new module is listed in `PlatformModules.Names`

## If it adds a guard

- [ ] The guard was broken deliberately and observed to fail, then restored.
      Changing a constant and its expectation together proves nothing.

## Anything a reviewer should look at first

Point at the part you are least sure about.
