using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.BuildingBlocks.Persistence;
using Npgsql;

namespace GiftCardPlatform.Modules.Organizations.Application;

/// <summary>
/// Resolves authentication's active membership without exposing the
/// Organizations DbContext or entity outside its owning module.
/// </summary>
internal sealed class ActiveMembershipResolver(
    IDatabaseConnectionFactory connectionFactory,
    ISessionContextWriter sessionContextWriter,
    IExecutionContext executionContext) : IActiveMembershipResolver
{
    public async Task<ActiveMembershipResolution?> ResolveActiveMembershipAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || organizationId == Guid.Empty)
        {
            return null;
        }

        // Authentication establishes a non-authenticated candidate context
        // first. It is enough to set the RLS tenant, but cannot authorize an
        // application operation until this lookup succeeds.
        if (executionContext.IsAuthenticated ||
            executionContext.UserId != userId ||
            executionContext.ActiveOrganizationId != organizationId ||
            executionContext.ActiveMembershipId is not null)
        {
            return null;
        }

        // Authentication occurs before the request's application transaction.
        // Use an independent short-lived connection so its completed lookup
        // transaction cannot remain attached to a scoped module DbContext that
        // the endpoint will use later in the same request.
        await using var connection = await connectionFactory
            .CreateOpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await sessionContextWriter
            .WriteAsync(connection, transaction, executionContext, cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select membership.id, organization.root_organization_id
            from organizations.organization_memberships membership
            join organizations.organizations organization
              on organization.id = membership.organization_id
            where membership.organization_id = @organization_id
              and membership.user_id = @user_id
              and membership.status = 'Active'
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("user_id", userId);

        ActiveMembershipResolution? resolution = null;
        await using (var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                resolution = new ActiveMembershipResolution(
                    reader.GetGuid(0),
                    reader.GetGuid(1));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resolution;
    }
}
