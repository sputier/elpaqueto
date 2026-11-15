using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElPaqueto.Security.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    KeyId = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    SecretHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Permissions = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.CheckConstraint("CK_ApiKeys_KeyId", "length(\"KeyId\") = 12");
                    table.CheckConstraint("CK_ApiKeys_Permissions", "\"Permissions\" BETWEEN 0 AND 7");
                    table.CheckConstraint("CK_ApiKeys_SecretHash", "length(\"SecretHash\") = 32");
                    table.ForeignKey(
                        name: "FK_ApiKeys_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyId",
                table: "ApiKeys",
                column: "KeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_OwnerUserId",
                table: "ApiKeys",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys");
        }
    }
}
