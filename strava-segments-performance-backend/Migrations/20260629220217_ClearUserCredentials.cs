using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StravaSegmentsPerformanceBackend.Migrations
{
    /// <inheritdoc />
    public partial class ClearUserCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"Users\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
