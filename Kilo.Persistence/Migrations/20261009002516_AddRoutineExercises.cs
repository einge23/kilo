using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kilo.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoutineExercises : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "access_scope_id",
                schema: "public",
                table: "exercises",
                type: "integer",
                nullable: false,
                computedColumnSql: "coalesce(user_id, 0)",
                stored: true);

            migrationBuilder.AddUniqueConstraint(
                name: "exercises_id_access_scope_key",
                schema: "public",
                table: "exercises",
                columns: new[] { "id", "access_scope_id" });

            migrationBuilder.CreateTable(
                name: "routine_exercises",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    routine_id = table.Column<int>(type: "integer", nullable: false),
                    exercise_id = table.Column<int>(type: "integer", nullable: false),
                    exercise_scope_id = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    default_rest_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 120),
                    archived_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("routine_exercises_pkey", x => x.id);
                    table.UniqueConstraint("routine_exercises_id_user_id_key", x => new { x.id, x.user_id });
                    table.CheckConstraint("routine_exercises_default_rest_seconds_check", "default_rest_seconds >= 0");
                    table.CheckConstraint("routine_exercises_exercise_scope_check", "exercise_scope_id = 0 OR exercise_scope_id = user_id");
                    table.CheckConstraint("routine_exercises_position_check", "position > 0");
                    table.ForeignKey(
                        name: "routine_exercises_exercise_scope_fkey",
                        columns: x => new { x.exercise_id, x.exercise_scope_id },
                        principalSchema: "public",
                        principalTable: "exercises",
                        principalColumns: new[] { "id", "access_scope_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "routine_exercises_routine_owner_fkey",
                        columns: x => new { x.routine_id, x.user_id },
                        principalSchema: "public",
                        principalTable: "routines",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "routine_exercises_user_id_fkey",
                        column: x => x.user_id,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_routine_exercises_exercise_id_exercise_scope_id",
                schema: "public",
                table: "routine_exercises",
                columns: new[] { "exercise_id", "exercise_scope_id" });

            migrationBuilder.CreateIndex(
                name: "IX_routine_exercises_routine_id_user_id",
                schema: "public",
                table: "routine_exercises",
                columns: new[] { "routine_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_routine_exercises_user_id",
                schema: "public",
                table: "routine_exercises",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "routine_exercises_active_position",
                schema: "public",
                table: "routine_exercises",
                columns: new[] { "routine_id", "position" },
                unique: true,
                filter: "archived_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "routine_exercises",
                schema: "public");

            migrationBuilder.DropUniqueConstraint(
                name: "exercises_id_access_scope_key",
                schema: "public",
                table: "exercises");

            migrationBuilder.DropColumn(
                name: "access_scope_id",
                schema: "public",
                table: "exercises");
        }
    }
}
