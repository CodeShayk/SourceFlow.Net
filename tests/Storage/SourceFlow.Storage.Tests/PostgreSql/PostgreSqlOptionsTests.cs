using SourceFlow.Storage.PostgreSQL;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>Option validation and embedded scripts; no server needed.</summary>
[Trait("Category", "Unit")]
[Trait("Provider", "PostgreSQL")]
public sealed class PostgreSqlOptionsTests
{
    [Theory]
    [Trait("Id", "UT-PG-01")]
    [InlineData("Public")]
    [InlineData("x; DROP TABLE y")]
    [InlineData("")]
    [InlineData("a-b")]
    public void SchemaName_MustBeAPlainIdentifier(string schema)
    {
        Should.Throw<InvalidOperationException>(() => new PostgreSqlStore(new PostgreSqlStorageOptions { ConnectionString = "Host=x", Schema = schema }));
    }

    [Fact]
    [Trait("Id", "UT-PG-02")]
    public void ConnectionStringOrDataSource_IsRequired_AndTheChannelIsValidated()
    {
        Should.Throw<InvalidOperationException>(() => new PostgreSqlStore(new PostgreSqlStorageOptions()));
        Should.Throw<InvalidOperationException>(() => new PostgreSqlStore(new PostgreSqlStorageOptions { ConnectionString = "Host=x", NotificationChannel = "bad channel" }));
        Should.Throw<InvalidOperationException>(() => new PostgreSqlStore(new PostgreSqlStorageOptions { ConnectionString = "Host=x", ListenReconnectDelay = TimeSpan.Zero }));
    }

    [Fact]
    [Trait("Id", "UT-PG-04")]
    [Trait("Covers", "ADR-022")]
    public void RowLevelSecurity_RequiresASystemConnection_ForTheCrossTenantLog()
    {
        var options = new PostgreSqlStorageOptions { ConnectionString = "Host=x", TenantIsolation = TenantIsolation.RowLevelSecurity };

        Should.Throw<InvalidOperationException>(() => new PostgreSqlStore(options)).Message.ShouldContain("BYPASSRLS");
        options.SystemConnectionString = "Host=x;Username=system";
        Should.NotThrow(() => new PostgreSqlStore(options).DisposeAsync().AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    [Trait("Id", "UT-PG-03")]
    public void EmbeddedScripts_AreVersionedAndChecksummed_IndependentOfLineEndings()
    {
        PostgreSqlSchemaMigrator.EmbeddedScripts.Select(s => $"V{s.Version:D3}__{s.Name}").ShouldBe(["V001__initial", "V002__tenant_policies", "V003__commit_assertions", "V004__read_model_generations"]);
        MigrationScript initial = PostgreSqlSchemaMigrator.EmbeddedScripts[0];
        initial.Version.ShouldBe(1);
        initial.Name.ShouldBe("initial");
        initial.Checksum.Length.ShouldBe(64);

        string windows = initial.Sql.ReplaceLineEndings("\r\n");
        (initial with { Sql = windows }).Checksum.ShouldBe(initial.Checksum);
        (initial with { Sql = initial.Sql + " " }).Checksum.ShouldNotBe(initial.Checksum);
    }
}
