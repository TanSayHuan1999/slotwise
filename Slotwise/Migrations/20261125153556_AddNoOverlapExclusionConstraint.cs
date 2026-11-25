using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slotwise.Migrations
{
    /// <inheritdoc />
    public partial class AddNoOverlapExclusionConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The database-level half of the no-double-booking guarantee.
            // btree_gist lets a scalar equality (StaffMemberId) be combined
            // with a range overlap (tstzrange &&) in one GiST index.
            // The partial WHERE limits it to Confirmed bookings (Status = 0),
            // so cancelled / completed / no-show bookings never block a slot.
            // tstzrange's default bounds are [), matching the app's
            // half-open [StartUtc, EndUtc) intervals: back-to-back bookings
            // (one ends exactly when the next starts) do not conflict.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql("""
                ALTER TABLE "Bookings"
                ADD CONSTRAINT "EX_Bookings_NoOverlap"
                EXCLUDE USING gist (
                    "StaffMemberId" WITH =,
                    tstzrange("StartUtc", "EndUtc") WITH &&
                )
                WHERE ("Status" = 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Bookings" DROP CONSTRAINT IF EXISTS "EX_Bookings_NoOverlap";
                """);
        }
    }
}
