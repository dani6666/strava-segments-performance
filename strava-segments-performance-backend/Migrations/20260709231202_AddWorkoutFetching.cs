using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace StravaSegmentsPerformanceBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkoutFetching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    StravaActivityId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SportType = table.Column<string>(type: "text", nullable: false),
                    StartDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DistanceMeters = table.Column<double>(type: "double precision", nullable: false),
                    MovingTimeSeconds = table.Column<int>(type: "integer", nullable: false),
                    ElapsedTimeSeconds = table.Column<int>(type: "integer", nullable: false),
                    DetailsFetched = table.Column<bool>(type: "boolean", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SegmentEfforts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActivityId = table.Column<int>(type: "integer", nullable: false),
                    StravaSegmentEffortId = table.Column<long>(type: "bigint", nullable: false),
                    StravaSegmentId = table.Column<long>(type: "bigint", nullable: false),
                    SegmentName = table.Column<string>(type: "text", nullable: false),
                    ElapsedTimeSeconds = table.Column<int>(type: "integer", nullable: false),
                    AverageHeartRate = table.Column<double>(type: "double precision", nullable: true),
                    StartDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentEfforts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkoutFetchStatuses",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: true),
                    ActivitiesProcessed = table.Column<int>(type: "integer", nullable: false),
                    TotalToProcess = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutFetchStatuses", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_UserId_StravaActivityId",
                table: "Activities",
                columns: new[] { "UserId", "StravaActivityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SegmentEfforts_StravaSegmentEffortId",
                table: "SegmentEfforts",
                column: "StravaSegmentEffortId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Activities");

            migrationBuilder.DropTable(
                name: "SegmentEfforts");

            migrationBuilder.DropTable(
                name: "WorkoutFetchStatuses");
        }
    }
}
