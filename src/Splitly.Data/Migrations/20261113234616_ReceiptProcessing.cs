using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Splitly.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "FailureReason",
                table: "Receipts",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "Receipts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ExpenseId",
                table: "Receipts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Merchant",
                table: "Receipts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Receipts",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProcessingStartedAt",
                table: "Receipts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StatedTotal",
                table: "Receipts",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "ExpenseId",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "Merchant",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "ProcessingStartedAt",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "StatedTotal",
                table: "Receipts");

            migrationBuilder.AlterColumn<string>(
                name: "FailureReason",
                table: "Receipts",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
