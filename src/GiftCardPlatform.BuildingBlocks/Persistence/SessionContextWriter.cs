using GiftCardPlatform.BuildingBlocks.Execution;
using Npgsql;

namespace GiftCardPlatform.BuildingBlocks.Persistence;

/// <summary>
/// Establishes the PostgreSQL session context that Row-Level Security policies
/// will read (ADR-020).
/// </summary>
public interface ISessionContextWriter
{
    Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IExecutionContext executionContext,
        CancellationToken cancellationToken);
}

/// <summary>
/// Writes the execution context into transaction-local PostgreSQL settings using
/// <c>set_config(..., is_local: true)</c>, which is the parameterised equivalent
/// of <c>SET LOCAL</c>.
///
/// Why transaction-local rather than session-local (ADR-020):
///   * The values are discarded when the transaction ends, so a pooled
///     connection can never carry one caller's context into the next request.
///   * It applies to reads as well as writes, so it cannot be replaced by a
///     SaveChangesInterceptor, which only runs on write.
///   * Parameters are bound rather than interpolated, so the values cannot be
///     used for SQL injection.
///
/// Tenant-owned module tables and the audit store consume these settings in
/// PostgreSQL RLS policies. Every database operation on those tables must
/// therefore run inside a transaction with this context established.
/// </summary>
public sealed class SessionContextWriter : ISessionContextWriter
{
    public const string UserIdSetting = "app.user_id";
    public const string OrganizationIdSetting = "app.organization_id";
    public const string PlatformOperatorSetting = "app.is_platform_operator";
    public const string ClaimInvitationIdSetting = "app.claim_invitation_id";
    public const string ShareIdSetting = "app.share_id";
    public const string PaymentTokenIdSetting = "app.payment_token_id";
    public const string PaymentCodeHashSetting = "app.payment_code_hash";
    public const string PosClientIdSetting = "app.pos_client_id";

    /// <summary>
    /// Acting partner API client (ADR-053). The read-only credential-lookup
    /// escape, app.is_partner_credential_lookup, is deliberately not written
    /// here: it is set with a transaction-local set_config on the guarded
    /// exchange path only, exactly as app.is_initial_admin_bootstrap is.
    /// </summary>
    public const string PartnerClientIdSetting = "app.partner_client_id";

    private const string Sql = """
        select
            set_config(@user_id_key, @user_id, true),
            set_config(@organization_id_key, @organization_id, true),
            set_config(@platform_operator_key, @is_platform_operator, true),
            set_config(@claim_invitation_id_key, @claim_invitation_id, true),
            set_config(@share_id_key, @share_id, true),
            set_config(@payment_token_id_key, @payment_token_id, true),
            set_config(@payment_code_hash_key, @payment_code_hash, true),
            set_config(@pos_client_id_key, @pos_client_id, true),
            set_config(@partner_client_id_key, @partner_client_id, true)
        """;

    public async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(executionContext);

        await using var command = new NpgsqlCommand(Sql, connection, transaction);

        command.Parameters.AddWithValue("user_id_key", UserIdSetting);
        command.Parameters.AddWithValue("organization_id_key", OrganizationIdSetting);
        command.Parameters.AddWithValue("platform_operator_key", PlatformOperatorSetting);
        command.Parameters.AddWithValue("claim_invitation_id_key", ClaimInvitationIdSetting);
        command.Parameters.AddWithValue("share_id_key", ShareIdSetting);
        command.Parameters.AddWithValue("payment_token_id_key", PaymentTokenIdSetting);
        command.Parameters.AddWithValue("payment_code_hash_key", PaymentCodeHashSetting);
        command.Parameters.AddWithValue("pos_client_id_key", PosClientIdSetting);
        command.Parameters.AddWithValue("partner_client_id_key", PartnerClientIdSetting);

        // set_config requires text; an empty string represents "not set".
        command.Parameters.AddWithValue("user_id", executionContext.UserId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("organization_id", executionContext.ActiveOrganizationId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("is_platform_operator", executionContext.IsPlatformOperator ? "true" : "false");
        command.Parameters.AddWithValue(
            "claim_invitation_id",
            executionContext.ClaimInvitationId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue(
            "share_id",
            executionContext.ShareId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue(
            "payment_token_id",
            executionContext.PaymentTokenId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue(
            "payment_code_hash",
            executionContext.PaymentCodeHash ?? string.Empty);
        command.Parameters.AddWithValue(
            "pos_client_id",
            executionContext.PosClientId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue(
            "partner_client_id",
            executionContext.PartnerClientId?.ToString() ?? string.Empty);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
