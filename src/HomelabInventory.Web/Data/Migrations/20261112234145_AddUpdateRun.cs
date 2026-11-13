using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomelabInventory.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdateRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpdateRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrackedAppId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImageTag = table.Column<string>(type: "TEXT", nullable: true),
                    Log = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpdateRuns_TrackedApps_TrackedAppId",
                        column: x => x.TrackedAppId,
                        principalTable: "TrackedApps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateRuns_TrackedAppId_StartedAtUtc",
                table: "UpdateRuns",
                columns: new[] { "TrackedAppId", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpdateRuns");
        }
    }
}
