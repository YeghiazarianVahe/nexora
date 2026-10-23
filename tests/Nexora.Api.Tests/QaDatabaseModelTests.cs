using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Api.Tests;

// Offline Npgsql schema validation: no connection is opened and no migration is applied.
public sealed class QaDatabaseModelTests
{
    private static NexoraDbContext Context() => new(new DbContextOptionsBuilder<NexoraDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=qa_offline;Username=qa").Options);

    [Fact]
    public void Current_model_matches_the_checked_in_migration_snapshot()
    {
        using var db = Context();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public void All_migrations_generate_an_idempotent_PostgreSQL_script_offline()
    {
        using var db = Context();
        var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        sql.Should().Contain("__EFMigrationsHistory").And.Contain("endpoint_group_members").And.Contain("audit_entries").And.Contain("CREATE TRIGGER");
        sql.Should().NotContain("DROP TABLE");
    }

    [Fact]
    public void Duplicate_address_check_translates_to_PostgreSQL_lower()
    {
        using var db = Context();
        var address = "QA-PC";
        var sql = db.Endpoints.Where(e => e.Hostname.ToLower() == address.ToLower()).ToQueryString();
        sql.Should().Contain("lower(");
    }
}
