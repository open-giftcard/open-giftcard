using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Audit.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class AuditCheckpointTests(PlatformApiFixture fixture)
{
    private sealed class FailingAuditCheckpointSigner : IAuditCheckpointSigner
    {
        public Task<AuditCheckpointSignature> SignDigestAsync(
            ReadOnlyMemory<byte> digest,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated KMS outage.");
    }

    [Fact]
    public async Task Checkpoint_pipeline_seals_witnesses_and_verifies_real_audit_rows()
    {
        await AppendAuditRecordAsync("checkpoint.pipeline.first");
        await AppendAuditRecordAsync("checkpoint.pipeline.second");

        await ProcessUntilIdleAsync();
        var verification = await VerifyAsync();

        Assert.True(verification.IsValid, verification.FailureCode);
        Assert.True(verification.ManifestCount > 0);
        Assert.Equal(verification.ManifestCount, verification.SignedCount);
        Assert.Equal(verification.ManifestCount, verification.WitnessedCount);

        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select count(*)
            from audit.audit_checkpoints checkpoint
            join audit.audit_checkpoint_seals seal on seal.checkpoint_id = checkpoint.id
            join audit.audit_checkpoint_witnesses witness on witness.checkpoint_id = checkpoint.id
            where octet_length(checkpoint.merkle_root) = 32
              and octet_length(checkpoint.manifest_digest) = 32
              and octet_length(seal.signature) = 64
              and witness.manifest_digest = checkpoint.manifest_digest
            """,
            connection);
        Assert.Equal(
            verification.ManifestCount,
            Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Verification_detects_a_committed_record_changed_by_a_privileged_actor()
    {
        await AppendAuditRecordAsync("checkpoint.tamper.target");
        await ProcessUntilIdleAsync();

        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        Guid recordId;
        string originalOperation;
        await using var readTransaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "select set_config('app.is_platform_operator', 'true', true)",
            connection,
            readTransaction))
        {
            await context.ExecuteNonQueryAsync();
        }

        await using (var select = new NpgsqlCommand(
            """
            select record.id, record.operation
            from audit.audit_records record
            where record.audit_sequence <= (
                select max(last_sequence) from audit.audit_checkpoints)
            order by record.audit_sequence
            limit 1
            """,
            connection,
            readTransaction))
        await using (var reader = await select.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            recordId = reader.GetGuid(0);
            originalOperation = reader.GetString(1);
        }
        await readTransaction.CommitAsync();

        try
        {
            await SetOperationAsync(connection, recordId, originalOperation + ".tampered");
            var verification = await VerifyAsync();
            Assert.False(verification.IsValid);
            Assert.Equal("checkpoint_records_invalid", verification.FailureCode);
        }
        finally
        {
            await SetOperationAsync(connection, recordId, originalOperation);
        }

        Assert.True((await VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Verification_detects_changed_external_witness_bytes()
    {
        await AppendAuditRecordAsync("checkpoint.witness.target");
        await ProcessUntilIdleAsync();

        string reference;
        await using (var connection = new NpgsqlConnection(fixture.MigratorConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "select reference from audit.audit_checkpoint_witnesses order by witnessed_at_utc limit 1",
                connection);
            reference = (string)(await command.ExecuteScalarAsync())!;
        }

        var witness = fixture.Factory.Services.GetRequiredService<TestAuditCheckpointWitness>();
        var original = witness.ReplaceForTest(reference, "changed"u8.ToArray());
        try
        {
            var verification = await VerifyAsync();
            Assert.False(verification.IsValid);
            Assert.Equal("checkpoint_witness_invalid", verification.FailureCode);
        }
        finally
        {
            witness.ReplaceForTest(reference, original);
        }

        Assert.True((await VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Verification_detects_external_evidence_missing_from_the_database()
    {
        await AppendAuditRecordAsync("checkpoint.missing.database.target");
        await ProcessUntilIdleAsync();

        var witness = fixture.Factory.Services.GetRequiredService<TestAuditCheckpointWitness>();
        const string orphanReference = "019c0598670070008000000000ff0000.json";
        witness.AddForTest(orphanReference, "signed-checkpoint-no-longer-in-database"u8.ToArray());
        try
        {
            var verification = await VerifyAsync();
            Assert.False(verification.IsValid);
            Assert.Equal("checkpoint_witness_inventory_invalid", verification.FailureCode);
        }
        finally
        {
            witness.RemoveForTest(orphanReference);
        }

        Assert.True((await VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Runtime_role_can_use_sequence_but_cannot_mutate_checkpoint_evidence()
    {
        await AppendAuditRecordAsync("checkpoint.privilege.target");
        await ProcessUntilIdleAsync();

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using (var sequence = new NpgsqlCommand(
            "select nextval('audit.audit_record_sequence')",
            connection))
        {
            Assert.True((long)(await sequence.ExecuteScalarAsync())! > 0);
        }

        await using var update = new NpgsqlCommand(
            "update audit.audit_checkpoints set record_count = record_count where false",
            connection);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => update.ExecuteNonQueryAsync());
        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Sealer_waits_for_inflight_writer_and_includes_its_committed_sequence()
    {
        await ProcessUntilIdleAsync();
        await using var writerConnection = await fixture.OpenAppConnectionAsync();
        await using var writerTransaction = await writerConnection.BeginTransactionAsync();
        await MembershipTestSupport.SetSessionContextAsync(
            writerConnection,
            writerTransaction,
            organizationId: null,
            isPlatformOperator: true);
        await using (var writerLock = new NpgsqlCommand(
            "select pg_advisory_xact_lock_shared(4697588874431775817)",
            writerConnection,
            writerTransaction))
        {
            await writerLock.ExecuteNonQueryAsync();
        }

        long committedSequence;
        await using (var insert = new NpgsqlCommand(
            """
            insert into audit.audit_records (
                id, actor_user_id, actor_type, organization_scope_id,
                operation, entity_type, entity_id, outcome,
                correlation_id, occurred_at_utc, metadata, actor_membership_id)
            values (
                @id, @actor_user_id, 'System', null,
                'checkpoint.concurrent.writer', 'AuditCheckpointTest', @entity_id, 'Success',
                @correlation_id, now(), null, null)
            returning audit_sequence
            """,
            writerConnection,
            writerTransaction))
        {
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("actor_user_id", SystemActorIds.AuditCheckpoint);
            insert.Parameters.AddWithValue("entity_id", Guid.CreateVersion7().ToString());
            insert.Parameters.AddWithValue("correlation_id", Guid.CreateVersion7());
            committedSequence = (long)(await insert.ExecuteScalarAsync())!;
        }

        var sealing = ProcessSinglePassAsync();
        var early = await Task.WhenAny(sealing, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(sealing, early);

        await writerTransaction.CommitAsync();
        var result = await sealing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.ManifestCreated);
        await ProcessUntilIdleAsync();
        Assert.True((await VerifyAsync()).IsValid);

        await using var evidenceConnection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await evidenceConnection.OpenAsync();
        await using var boundary = new NpgsqlCommand(
            "select count(*) from audit.audit_checkpoints where last_sequence >= @sequence",
            evidenceConnection);
        boundary.Parameters.AddWithValue("sequence", committedSequence);
        Assert.Equal(1L, (long)(await boundary.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Signer_outage_delays_sealing_but_does_not_block_audit_writes()
    {
        await ProcessUntilIdleAsync();
        await AppendAuditRecordAsync("checkpoint.signer.outage.pending");

        using var failingFactory = fixture.Factory.WithWebHostBuilder(webHost =>
            webHost.ConfigureServices(services =>
            {
                services.RemoveAll<IAuditCheckpointSigner>();
                services.AddSingleton<IAuditCheckpointSigner, FailingAuditCheckpointSigner>();
            }));

        var first = await ProcessSinglePassAsync(failingFactory.Services);
        Assert.True(first.ManifestCreated);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProcessSinglePassAsync(failingFactory.Services));

        var sequence = await AppendAuditRecordAsync("checkpoint.signer.outage.business-write");
        Assert.True(sequence > 0);

        await ProcessUntilIdleAsync();
        Assert.True((await VerifyAsync()).IsValid);
    }

    private async Task<long> AppendAuditRecordAsync(string operation)
    {
        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command(
            """
            insert into audit.audit_records (
                id, actor_user_id, actor_type, organization_scope_id,
                operation, entity_type, entity_id, outcome,
                correlation_id, occurred_at_utc, metadata, actor_membership_id)
            values (
                @id, @actor_user_id, 'System', null,
                @operation, 'AuditCheckpointTest', @entity_id, 'Success',
                @correlation_id, now(), null, null)
            returning audit_sequence
            """);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("actor_user_id", SystemActorIds.AuditCheckpoint);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("entity_id", Guid.CreateVersion7().ToString());
        command.Parameters.AddWithValue("correlation_id", Guid.CreateVersion7());
        var sequence = (long)(await command.ExecuteScalarAsync())!;
        await session.CommitAsync();
        return sequence;
    }

    private async Task ProcessUntilIdleAsync()
    {
        for (var pass = 0; pass < 12; pass++)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            SetSystemContext(scope.ServiceProvider);
            var result = await scope.ServiceProvider
                .GetRequiredService<IAuditCheckpointProcessor>()
                .ProcessNextAsync(10_000, CancellationToken.None);
            if (!result.ManifestCreated && !result.SignatureCreated && !result.WitnessPublished)
            {
                return;
            }
        }

        throw new InvalidOperationException("The checkpoint pipeline did not become idle.");
    }

    private async Task<AuditCheckpointPassResult> ProcessSinglePassAsync()
        => await ProcessSinglePassAsync(fixture.Factory.Services);

    private static async Task<AuditCheckpointPassResult> ProcessSinglePassAsync(
        IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        SetSystemContext(scope.ServiceProvider);
        return await scope.ServiceProvider
            .GetRequiredService<IAuditCheckpointProcessor>()
            .ProcessNextAsync(10_000, CancellationToken.None);
    }

    private async Task<AuditCheckpointVerificationResult> VerifyAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        SetSystemContext(scope.ServiceProvider);
        return await scope.ServiceProvider
            .GetRequiredService<IAuditCheckpointProcessor>()
            .VerifyAsync(CancellationToken.None);
    }

    private static void SetSystemContext(IServiceProvider serviceProvider)
    {
        var context = serviceProvider.GetRequiredService<MutableExecutionContext>();
        context.SetCorrelationId(Guid.CreateVersion7());
        context.SetSystem(SystemActorIds.AuditCheckpoint, []);
    }

    private static async Task SetOperationAsync(
        NpgsqlConnection connection,
        Guid recordId,
        string operation)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "select set_config('app.is_platform_operator', 'true', true)",
            connection,
            transaction))
        {
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "update audit.audit_records set operation = @operation where id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("id", recordId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }
}
