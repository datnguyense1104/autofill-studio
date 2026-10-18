using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoFill.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeDeletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "recipes",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "recipes");
        }
    }
}
