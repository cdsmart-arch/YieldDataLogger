using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YieldDataLogger.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Instruments",
                columns: table => new
                {
                    CanonicalSymbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    InvestingPid = table.Column<int>(type: "int", nullable: true),
                    CnbcSymbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.CanonicalSymbol);
                });

            migrationBuilder.CreateTable(
                name: "PriceTicks",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TsUnix = table.Column<double>(type: "float", nullable: false),
                    Price = table.Column<double>(type: "float", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceTicks", x => new { x.Symbol, x.TsUnix });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_CnbcSymbol",
                table: "Instruments",
                column: "CnbcSymbol",
                unique: true,
                filter: "[CnbcSymbol] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_InvestingPid",
                table: "Instruments",
                column: "InvestingPid",
                unique: true,
                filter: "[InvestingPid] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Instruments");

            migrationBuilder.DropTable(
                name: "PriceTicks");
        }
    }
}
