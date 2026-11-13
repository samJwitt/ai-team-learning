using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabInventory.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrackedAppId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImageTag = table.Column<string>(type: "TEXT", nullable: true),
                    TargetHost = table.Column<string>(type: "TEXT", nullable: true),
                    HostPort = table.Column<int>(type: "INTEGER", nullable: false),
                    OldHost = table.Column<string>(type: "TEXT", nullable: true),
                    OldPort = table.Column<int>(type: "INTEGER", nullable: false),
                    Log = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppOperations_TrackedAppId_StartedAtUtc",
                table: "AppOperations",
                columns: new[] { "TrackedAppId", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppOperations");
        }
    }
}
