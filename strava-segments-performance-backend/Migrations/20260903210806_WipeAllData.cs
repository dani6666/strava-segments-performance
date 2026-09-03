using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StravaSegmentsPerformanceBackend.Migrations
{
    /// <inheritdoc />
    public partial class WipeAllData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"SegmentEfforts\"");
            migrationBuilder.Sql("DELETE FROM \"Activities\"");
            migrationBuilder.Sql("DELETE FROM \"WorkoutFetchStatuses\"");
            migrationBuilder.Sql("DELETE FROM \"Users\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
