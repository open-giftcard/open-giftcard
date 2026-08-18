using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Payments.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class PaymentProvisionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void The_window_runs_from_creation_and_lasts_exactly_as_configured()
    {
        var provision = CreateProvision();

        Assert.Equal(PaymentProvisionState.Active, provision.State);
        Assert.Null(provision.SettledAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(2), provision.ExpiresAtUtc - provision.CreatedAtUtc);
    }

    [Fact]
    public void A_hold_stops_reserving_value_at_its_deadline_without_being_settled()
    {
        var provision = CreateProvision();

        Assert.True(provision.IsHolding(provision.ExpiresAtUtc.AddTicks(-10)));
        Assert.False(provision.IsHolding(provision.ExpiresAtUtc));
        // Availability is clock-derived, so the sweep has not had to run.
        Assert.Equal(PaymentProvisionState.Active, provision.State);
    }

    [Fact]
    public void Releasing_a_hold_settles_it_and_stops_it_reserving_value()
    {
        var cancelled = CreateProvision();
        var expired = CreateProvision();

        cancelled.Cancel(Now.AddSeconds(30));
        expired.Expire(Now.AddMinutes(3));

        Assert.Equal(PaymentProvisionState.Cancelled, cancelled.State);
        Assert.Equal(PaymentProvisionState.Expired, expired.State);
        Assert.NotNull(cancelled.SettledAtUtc);
        Assert.NotNull(expired.SettledAtUtc);
        Assert.False(cancelled.IsHolding(Now.AddSeconds(31)));
    }

    [Fact]
    public void A_settled_hold_is_terminal_and_cannot_be_released_twice()
    {
        var provision = CreateProvision();
        provision.Cancel(Now.AddSeconds(30));

        // Otherwise a released hold could be revived and reserve value again.
        Assert.Throws<ConflictException>(() => provision.Cancel(Now.AddSeconds(40)));
        Assert.Throws<ConflictException>(() => provision.Expire(Now.AddMinutes(3)));
        Assert.Equal(PaymentProvisionState.Cancelled, provision.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000_001)]
    [InlineData(0.00001)]
    public void An_unusable_amount_is_refused(decimal amount) =>
        Assert.Throws<ValidationFailedException>(() => CreateProvision(amount: amount));

    [Fact]
    public void A_blank_pos_reference_is_recorded_as_absent_rather_than_empty()
    {
        Assert.Null(CreateProvision(posTransactionReference: "   ").PosTransactionReference);
        Assert.Equal("SALE-1", CreateProvision(posTransactionReference: " SALE-1 ")
            .PosTransactionReference);
    }

    [Fact]
    public void An_over_long_pos_reference_is_refused() =>
        Assert.Throws<ValidationFailedException>(() => CreateProvision(
            posTransactionReference: new string('x', 65)));

    [Fact]
    public void Every_identifier_in_the_payment_scope_is_required() =>
        Assert.Throws<ValidationFailedException>(() => PaymentProvision.Create(
            Guid.CreateVersion7(),
            Guid.Empty,
            Guid.CreateVersion7(),
            "GC-TEST",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "STORE-1",
            null,
            10m,
            "TRY",
            Now,
            120));

    private static PaymentProvision CreateProvision(
        decimal amount = 10m,
        string? posTransactionReference = null) =>
        PaymentProvision.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "GC-TEST",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "STORE-1",
            posTransactionReference,
            amount,
            "TRY",
            Now,
            windowSeconds: 120);

    [Fact]
    public void Confirmation_posts_any_positive_amount_up_to_the_held_ceiling()
    {
        var provision = CreateProvision(amount: 25m);
        var transactionId = Guid.CreateVersion7();

        provision.Confirm(20m, transactionId, Now.AddSeconds(30));

        Assert.Equal(PaymentProvisionState.Confirmed, provision.State);
        Assert.Equal(20m, provision.ConfirmedAmount);
        Assert.Equal(transactionId, provision.RedemptionLedgerTransactionId);
        Assert.True(provision.MatchesConfirmation(20m));
        Assert.False(provision.IsHolding(Now.AddSeconds(31)));
    }

    [Fact]
    public void Confirmation_above_the_hold_is_refused_without_settling_it()
    {
        var provision = CreateProvision(amount: 25m);

        Assert.Throws<ConflictException>(() =>
            provision.Confirm(25.01m, Guid.CreateVersion7(), Now.AddSeconds(30)));
        Assert.Equal(PaymentProvisionState.Active, provision.State);
        Assert.Null(provision.SettledAtUtc);
    }

    [Fact]
    public void Expired_or_already_settled_provisions_cannot_be_confirmed()
    {
        var expired = CreateProvision();
        var cancelled = CreateProvision();
        cancelled.Cancel(Now.AddSeconds(10));

        Assert.Throws<ConflictException>(() => expired.Confirm(
            10m,
            Guid.CreateVersion7(),
            expired.ExpiresAtUtc));
        Assert.Throws<ConflictException>(() => cancelled.Confirm(
            10m,
            Guid.CreateVersion7(),
            Now.AddSeconds(20)));
    }
}
