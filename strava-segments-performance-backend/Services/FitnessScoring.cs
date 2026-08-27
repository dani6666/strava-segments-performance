namespace StravaSegmentsPerformanceBackend.Services;

public sealed record SegmentEffortRecord(
    long StravaSegmentId,
    int ElapsedTimeSeconds,
    double? AverageHeartRate,
    int ActivityId,
    DateTime WorkoutStartUtc);

public sealed record FitnessTrendPoint(DateTime Date, double Score);

public static class FitnessScoring
{
    // An effort more than 2x its segment's median elapsed time is treated as a
    // mid-segment stop (coffee break, red light) rather than a genuine slow effort.
    private const double KStall = 2.0;

    // A workout needs at least this many scored efforts (post-stall-drop, post-N>=2-segment
    // filter) for its aggregate to be trustworthy - one or two repeated segments is too thin
    // a sample to call a fitness trend.
    private const int MinScoredEffortsPerWorkout = 3;

    public static IReadOnlyList<FitnessTrendPoint> Score(IEnumerable<SegmentEffortRecord> efforts)
    {
        var scoredEfforts = efforts
            .Where(e => e.AverageHeartRate.HasValue)
            .GroupBy(e => e.StravaSegmentId)
            .SelectMany(ScoreSegment)
            .ToList();

        var workoutScores = scoredEfforts
            .GroupBy(x => x.Effort.ActivityId)
            .Where(g => g.Count() >= MinScoredEffortsPerWorkout)
            .Select(g => new
            {
                Date = g.First().Effort.WorkoutStartUtc,
                Score = g.Sum(x => x.Weight * x.Percentile) / g.Sum(x => x.Weight)
            })
            .ToList();

        if (workoutScores.Count == 0)
        {
            return [];
        }

        var min = workoutScores.Min(w => w.Score);
        var max = workoutScores.Max(w => w.Score);

        return workoutScores
            .OrderBy(w => w.Date)
            .Select(w => new FitnessTrendPoint(
                w.Date,
                max > min ? 100.0 * (w.Score - min) / (max - min) : 50.0))
            .ToList();
    }

    // Per segment: drop stalls (elapsed > 2x the segment's raw median), then cost + percentile
    // the survivors. Weight is the survivors' own median elapsed time, so a dropped stall can
    // never inflate its own weight - it never enters the weight computation at all.
    private static IEnumerable<(SegmentEffortRecord Effort, double Weight, double Percentile)> ScoreSegment(
        IGrouping<long, SegmentEffortRecord> segmentEfforts)
    {
        var rawMedian = Median(segmentEfforts.Select(e => (double)e.ElapsedTimeSeconds));
        var survivors = segmentEfforts.Where(e => e.ElapsedTimeSeconds <= KStall * rawMedian).ToList();

        if (survivors.Count < 2)
        {
            yield break;
        }

        var weight = Median(survivors.Select(e => (double)e.ElapsedTimeSeconds));
        var costs = survivors.Select(e => e.AverageHeartRate!.Value * e.ElapsedTimeSeconds).ToList();
        var percentiles = ComputePercentiles(costs);

        for (var i = 0; i < survivors.Count; i++)
        {
            yield return (survivors[i], weight, percentiles[i]);
        }
    }

    // Percentile-with-average-rank: best effort (lowest cost) -> 100, worst -> 0,
    // tied costs share the same percentile so results are deterministic.
    private static List<double> ComputePercentiles(List<double> costs)
    {
        var n = costs.Count;
        var result = new List<double>(n);

        for (var i = 0; i < n; i++)
        {
            var worseCount = 0;
            var tiedCount = 0;
            for (var j = 0; j < n; j++)
            {
                if (j == i) continue;
                if (costs[j] > costs[i]) worseCount++;
                else if (costs[j] == costs[i]) tiedCount++;
            }

            result.Add(100.0 * (worseCount + tiedCount / 2.0) / (n - 1));
        }

        return result;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }
}
