using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kilo.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExercises : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exercises",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    brand_name = table.Column<string>(type: "text", nullable: true),
                    archived_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("exercises_pkey", x => x.id);
                    table.CheckConstraint("exercises_brand_name_check", "brand_name IS NULL OR (btrim(brand_name) <> '' AND length(brand_name) <= 100)");
                    table.CheckConstraint("exercises_name_check", "btrim(name) <> ''");
                    table.CheckConstraint("exercises_user_id_check", "user_id IS NULL OR user_id > 0");
                    table.ForeignKey(
                        name: "exercises_user_id_fkey",
                        column: x => x.user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "exercises_active_list",
                schema: "public",
                table: "exercises",
                columns: new[] { "user_id", "name" },
                filter: "archived_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exercises",
                schema: "public");
        }
    }
}
