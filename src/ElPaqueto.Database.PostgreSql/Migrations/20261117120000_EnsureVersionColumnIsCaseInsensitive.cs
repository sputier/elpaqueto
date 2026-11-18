using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElPaqueto.Database.PostgreSql.Migrations
{
    /// <summary>
    /// Makes sure that Packages.Version is a <c>citext</c> column.
    /// </summary>
    /// <remarks>
    /// Some databases created by BaGet or BaGetter have <c>Packages.Version</c> as <c>character varying(64)</c>
    /// although <c>FixVersionCaseSensitivity</c> is recorded as applied: a version with an uppercase letter
    /// (<c>1.0.0-Beta.1</c>) is stored in lower case but requested in its original case, so it was not found
    /// and could not be deleted (see https://github.com/bagetter/BaGetter/issues/239). The statement does
    /// nothing on a column that already is <c>citext</c>, and the model does not change.
    /// </remarks>
    public partial class EnsureVersionColumnIsCaseInsensitive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS citext;");
            migrationBuilder.Sql("ALTER TABLE \"Packages\" ALTER COLUMN \"Version\" TYPE citext;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: a case-sensitive Version column is the bug this migration fixes.
        }
    }
}
