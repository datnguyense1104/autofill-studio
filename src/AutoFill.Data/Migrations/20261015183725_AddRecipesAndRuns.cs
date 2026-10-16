using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoFill.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipesAndRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "job_id",
                table: "ai_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "recipes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    use_when = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipes", x => x.id);
                    table.ForeignKey(
                        name: "fk_recipes_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recipe_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    template_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    slots = table.Column<string>(type: "jsonb", nullable: false),
                    rules = table.Column<string>(type: "jsonb", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    check_missing = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipe_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_recipe_versions_files_template_file_id",
                        column: x => x.template_file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recipe_versions_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_file_ids = table.Column<string>(type: "jsonb", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    missing = table.Column<string>(type: "jsonb", nullable: false),
                    notes = table.Column<string>(type: "jsonb", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_runs_recipe_versions_recipe_version_id",
                        column: x => x.recipe_version_id,
                        principalTable: "recipe_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_runs_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "run_values",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot_id = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    warnings = table.Column<string>(type: "jsonb", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    origin = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    reviewed_value = table.Column<string>(type: "jsonb", nullable: true),
                    reviewed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_values", x => new { x.run_id, x.slot_id });
                    table.ForeignKey(
                        name: "fk_run_values_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_logs_job_id",
                table: "ai_logs",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipe_versions_recipe_id_number",
                table: "recipe_versions",
                columns: new[] { "recipe_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recipe_versions_template_file_id",
                table: "recipe_versions",
                column: "template_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipes_workspace_id_name",
                table: "recipes",
                columns: new[] { "workspace_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_runs_recipe_version_id",
                table: "runs",
                column: "recipe_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_runs_workspace_id_created_at",
                table: "runs",
                columns: new[] { "workspace_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_values");

            migrationBuilder.DropTable(
                name: "runs");

            migrationBuilder.DropTable(
                name: "recipe_versions");

            migrationBuilder.DropTable(
                name: "recipes");

            migrationBuilder.DropIndex(
                name: "ix_ai_logs_job_id",
                table: "ai_logs");

            migrationBuilder.DropColumn(
                name: "job_id",
                table: "ai_logs");
        }
    }
}
