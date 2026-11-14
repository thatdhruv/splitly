using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Splitly.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecurringExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OccurrenceCount",
                table: "RecurringExpenses",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "RecurringExpenses",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "RecurringExpenses",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_RecurringExpenseId_Date",
                table: "Expenses",
                columns: new[] { "RecurringExpenseId", "Date" },
                unique: true,
                filter: "[RecurringExpenseId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Expenses_RecurringExpenseId_Date",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "OccurrenceCount",
                table: "RecurringExpenses");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "RecurringExpenses");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "RecurringExpenses");
        }
    }
}
