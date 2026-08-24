using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Partners.Contracts;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GiftCardPlatform.Modules.Partners.Application;

internal sealed class PartnerMintQuota(
    ITransactionCoordinator transactionCoordinator,
    IOptions<PartnersOptions> options) : IPartnerMintQuota
{
    private const string ConsumeSql =
        """
        with quota_window as (
            select to_timestamp(
                floor(extract(epoch from statement_timestamp()) / @window_seconds)
                * @window_seconds) as started_at
        ),
        consumed as (
            insert into partners.mint_rate_windows (
                partner_api_client_id,
                window_started_at_utc,
                request_count)
            select @partner_client_id, quota_window.started_at, 1
            from quota_window
            on conflict (partner_api_client_id)
            do update set
                window_started_at_utc = excluded.window_started_at_utc,
                request_count = case
                    when partners.mint_rate_windows.window_started_at_utc
                            = excluded.window_started_at_utc
                    then partners.mint_rate_windows.request_count + 1
                    else 1
                end
            where partners.mint_rate_windows.window_started_at_utc
                    <> excluded.window_started_at_utc
               or partners.mint_rate_windows.request_count < @permit_limit
            returning 1
        )
        select exists(select 1 from consumed),
               greatest(
                   1,
                   ceil(extract(epoch from (
                       quota_window.started_at
                       + make_interval(secs => @window_seconds)
                       - statement_timestamp())))::integer)
        from quota_window;
        """;

    private readonly PartnersOptions settings = options.Value;

    public async Task<PartnerMintQuotaLease> TryAcquireAsync(
        Guid partnerClientId,
        CancellationToken cancellationToken)
    {
        if (partnerClientId == Guid.Empty)
        {
            throw new ArgumentException(
                "A partner API client id is required.",
                nameof(partnerClientId));
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            ConsumeSql,
            transaction.Transaction.Connection,
            transaction.Transaction);
        command.Parameters.AddWithValue("partner_client_id", partnerClientId);
        command.Parameters.AddWithValue("window_seconds", settings.MintWindowSeconds);
        command.Parameters.AddWithValue("permit_limit", settings.MintPermitLimit);

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Partner mint quota returned no result.");
        }

        var lease = new PartnerMintQuotaLease(reader.GetBoolean(0), reader.GetInt32(1));
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }
}
