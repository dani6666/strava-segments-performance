using Microsoft.EntityFrameworkCore;
using StravaSegmentsPerformanceBackend.Data;

namespace StravaSegmentsPerformanceBackend.Services;

public static class FitnessTrendQuery
{
    public static async Task<IReadOnlyList<FitnessTrendPoint>> GetForUserAsync(
        AppDbContext db, int userId, DateTime? from, DateTime? to)
    {
        var query = db.SegmentEfforts
            .Join(db.Activities, e => e.ActivityId, a => a.Id, (e, a) => new { Effort = e, Activity = a })
            .Where(x => x.Activity.UserId == userId);

        if (from is not null)
        {
            query = query.Where(x => x.Activity.StartDateUtc >= from.Value);
        }

        if (to is not null)
        {
            query = query.Where(x => x.Activity.StartDateUtc <= to.Value);
        }

        var records = await query
            .Select(x => new SegmentEffortRecord(
                x.Effort.StravaSegmentId,
                x.Effort.ElapsedTimeSeconds,
                x.Effort.AverageHeartRate,
                x.Effort.ActivityId,
                x.Activity.StartDateUtc))
            .ToListAsync();

        return FitnessScoring.Score(records);
    }
}
