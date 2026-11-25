using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slotwise.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantAdmins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAdmins", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantAdmins_Email",
                table: "TenantAdmins",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantAdmins_TenantId",
                table: "TenantAdmins",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantAdmins");
        }
    }
}
