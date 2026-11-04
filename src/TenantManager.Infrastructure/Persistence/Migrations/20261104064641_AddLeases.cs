using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "LeaseNumbers");

            migrationBuilder.CreateTable(
                name: "Leases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    PreviousLeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    MonthlyRent = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DepositAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    EscalationPct = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsMonthToMonth = table.Column<bool>(type: "bit", nullable: false),
                    ActivatedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    TerminatesOn = table.Column<DateOnly>(type: "date", nullable: true),
                    TerminationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LandlordName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    TemplateVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Leases_Leases_PreviousLeaseId",
                        column: x => x.PreviousLeaseId,
                        principalTable: "Leases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leases_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leases_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiryNoticeDays = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DefaultEscalationPct = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    MaxEscalationPct = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    MonthToMonthOnLapse = table.Column<bool>(type: "bit", nullable: false),
                    TerminationNoticeDays = table.Column<int>(type: "int", nullable: false),
                    LeaseTemplateVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeaseDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BlobKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaseDocuments_Leases_LeaseId",
                        column: x => x.LeaseId,
                        principalTable: "Leases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeaseExpiryNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DaysBefore = table.Column<int>(type: "int", nullable: false),
                    RecordedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseExpiryNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaseExpiryNotices_Leases_LeaseId",
                        column: x => x.LeaseId,
                        principalTable: "Leases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeaseSignatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Party = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SignerName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    SignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseSignatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaseSignatures_Leases_LeaseId",
                        column: x => x.LeaseId,
                        principalTable: "Leases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseDocuments_BlobKey",
                table: "LeaseDocuments",
                column: "BlobKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseDocuments_LeaseId",
                table: "LeaseDocuments",
                column: "LeaseId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseExpiryNotices_LeaseId_DaysBefore",
                table: "LeaseExpiryNotices",
                columns: new[] { "LeaseId", "DaysBefore" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leases_LeaseNumber_Version",
                table: "Leases",
                columns: new[] { "LeaseNumber", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leases_OwnerId_StartDate",
                table: "Leases",
                columns: new[] { "OwnerId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Leases_PreviousLeaseId",
                table: "Leases",
                column: "PreviousLeaseId",
                unique: true,
                filter: "[PreviousLeaseId] IS NOT NULL AND [Status] <> N'Terminated'");

            migrationBuilder.CreateIndex(
                name: "IX_Leases_TenantId_Status",
                table: "Leases",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Leases_UnitId_Status",
                table: "Leases",
                columns: new[] { "UnitId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseSignatures_LeaseId_Party",
                table: "LeaseSignatures",
                columns: new[] { "LeaseId", "Party" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeaseDocuments");

            migrationBuilder.DropTable(
                name: "LeaseExpiryNotices");

            migrationBuilder.DropTable(
                name: "LeaseSignatures");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Leases");

            migrationBuilder.DropSequence(
                name: "LeaseNumbers");
        }
    }
}
