using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Additive: a unique index on lower("Hostname"), so two rows can no longer differ only by case. It never deletes or merges data;
    /// if case-insensitive duplicates already exist the migration stops with the list and an operator resolves them first.
    /// The expression index is not expressible in the EF model, so the model snapshot is unchanged.
    /// </summary>
    public partial class UniqueLowerHostname : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
DECLARE dups text;
BEGIN
    SELECT string_agg(g.names, '; ' ORDER BY g.names) INTO dups
    FROM (
        SELECT string_agg('""' || ""Hostname"" || '""', ', ' ORDER BY ""Hostname"") AS names
        FROM endpoints
        GROUP BY lower(""Hostname"")
        HAVING count(*) > 1
    ) g;
    IF dups IS NOT NULL THEN
        RAISE EXCEPTION 'Cannot create IX_endpoints_Hostname_lower: host names that differ only by case exist: %. Rename or remove the extra rows, then run the migration again.', dups;
    END IF;
END $$;");

            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ""IX_endpoints_Hostname_lower"" ON endpoints (lower(""Hostname""));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_endpoints_Hostname_lower"";");
        }
    }
}
