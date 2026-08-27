using StravaSegmentsPerformanceBackend.Services;

namespace strava_segments_performance_backend_tests;

public class FitnessScoringTests
{
    [Fact]
    public void Score_FasterLowerHeartRateEffort_ScoresHigherOnSharedSegment()
    {
        // One shared segment, three activities, distinct costs -> exact 0/50/100 percentiles.
        var efforts = new[]
        {
            new SegmentEffortRecord(1, 200, 160, ActivityId: 1, new DateTime(2026, 1, 3)), // slowest + highest HR -> worst
            new SegmentEffortRecord(1, 150, 150, ActivityId: 2, new DateTime(2026, 1, 1)), // middle
            new SegmentEffortRecord(1, 100, 140, ActivityId: 3, new DateTime(2026, 1, 2))  // fastest + lowest HR -> best
        };

        var result = FitnessScoring.Score(efforts);

        var byActivityDate = result.ToDictionary(p => p.Date, p => p.Score);
        Assert.Equal(0.0, byActivityDate[new DateTime(2026, 1, 3)]);
        Assert.Equal(50.0, byActivityDate[new DateTime(2026, 1, 1)]);
        Assert.Equal(100.0, byActivityDate[new DateTime(2026, 1, 2)]);
    }

    [Fact]
    public void Score_EffortWithNullHeartRate_IsExcludedFromScoring()
    {
        var efforts = new[]
        {
            new SegmentEffortRecord(10, 100, 150, ActivityId: 1, new DateTime(2026, 1, 1)), // A
            new SegmentEffortRecord(10, 90, null, ActivityId: 2, new DateTime(2026, 1, 2)), // B - no HR, must be dropped
            new SegmentEffortRecord(10, 95, 140, ActivityId: 3, new DateTime(2026, 1, 3))   // C - better than A
        };

        var result = FitnessScoring.Score(efforts);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, p => p.Date == new DateTime(2026, 1, 2));
        var byDate = result.ToDictionary(p => p.Date, p => p.Score);
        Assert.Equal(0.0, byDate[new DateTime(2026, 1, 1)]);
        Assert.Equal(100.0, byDate[new DateTime(2026, 1, 3)]);
    }

    [Fact]
    public void Score_SingleEffortSegment_ContributesNothingButWorkoutStillScoredViaOtherSegment()
    {
        var efforts = new[]
        {
            // Segment 1: shared by X and Y -> scorable (n=2).
            new SegmentEffortRecord(1, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // X
            new SegmentEffortRecord(1, 120, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // Y
            // Segment 2: only X ever did it -> n=1, contributes nothing.
            new SegmentEffortRecord(2, 50, 130, ActivityId: 1, new DateTime(2026, 1, 1))
        };

        var result = FitnessScoring.Score(efforts);

        Assert.Equal(2, result.Count);
        var byDate = result.ToDictionary(p => p.Date, p => p.Score);
        // X's score matches what segment 1 alone would produce - segment 2 added nothing.
        Assert.Equal(100.0, byDate[new DateTime(2026, 1, 1)]);
        Assert.Equal(0.0, byDate[new DateTime(2026, 1, 2)]);
    }

    [Fact]
    public void Score_WorkoutWithNoRepeatedSegments_IsAbsentFromSeries()
    {
        var efforts = new[]
        {
            new SegmentEffortRecord(1, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // A
            new SegmentEffortRecord(1, 120, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // B
            new SegmentEffortRecord(99, 80, 120, ActivityId: 3, new DateTime(2026, 1, 3))  // Z - its only effort, unique segment -> no scorable data
        };

        var result = FitnessScoring.Score(efforts);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, p => p.Date == new DateTime(2026, 1, 3));
    }

    [Fact]
    public void Score_EffortFarSlowerThanSegmentMedian_IsDroppedAsStallAndDoesNotDragWorkoutDown()
    {
        var efforts = new[]
        {
            // Segment 5: P worse than Q normally.
            new SegmentEffortRecord(5, 110, 145, ActivityId: 1, new DateTime(2026, 1, 1)), // P
            new SegmentEffortRecord(5, 100, 140, ActivityId: 2, new DateTime(2026, 1, 2)), // Q - best on segment 5
            // Segment 6: Q also stopped mid-segment here (elapsed >> median) -> must be dropped
            // as a stall, not scored as a terrible effort, and must not contribute its huge
            // elapsed time as aggregation weight either.
            new SegmentEffortRecord(6, 500, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // Q's stall
            new SegmentEffortRecord(6, 100, 140, ActivityId: 3, new DateTime(2026, 1, 4)), // S
            new SegmentEffortRecord(6, 105, 140, ActivityId: 4, new DateTime(2026, 1, 5))  // T
        };

        var result = FitnessScoring.Score(efforts);

        var byDate = result.ToDictionary(p => p.Date, p => p.Score);
        Assert.Equal(4, result.Count);
        // If the stall had counted (even partially, e.g. via weight only), Q would land at 50,
        // not 100. It scores 100 - purely from segment 5 - proving the stall was fully excluded.
        Assert.Equal(100.0, byDate[new DateTime(2026, 1, 2)]); // Q
        Assert.Equal(0.0, byDate[new DateTime(2026, 1, 1)]);   // P
        Assert.Equal(100.0, byDate[new DateTime(2026, 1, 4)]); // S
        Assert.Equal(0.0, byDate[new DateTime(2026, 1, 5)]);   // T
    }

    [Fact]
    public void Score_TiedCosts_ProduceEqualPercentiles()
    {
        var efforts = new[]
        {
            new SegmentEffortRecord(20, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // M: cost 14000
            new SegmentEffortRecord(20, 140, 100, ActivityId: 2, new DateTime(2026, 1, 2)), // N: cost 14000 - tied with M
            new SegmentEffortRecord(20, 200, 150, ActivityId: 3, new DateTime(2026, 1, 3))  // O: cost 30000 - worse than both
        };

        var result = FitnessScoring.Score(efforts);

        var byDate = result.ToDictionary(p => p.Date, p => p.Score);
        Assert.Equal(byDate[new DateTime(2026, 1, 1)], byDate[new DateTime(2026, 1, 2)]); // tie -> identical scores
        Assert.Equal(100.0, byDate[new DateTime(2026, 1, 1)]);
        Assert.Equal(100.0, byDate[new DateTime(2026, 1, 2)]);
        Assert.Equal(0.0, byDate[new DateTime(2026, 1, 3)]);
    }

    [Fact]
    public void Score_SingleScoredWorkout_ScoresFifty()
    {
        // Same activity visits the same segment twice within one ride - the only workout
        // in the window, so min == max and the degenerate-window rule applies.
        var efforts = new[]
        {
            new SegmentEffortRecord(40, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)),
            new SegmentEffortRecord(40, 120, 150, ActivityId: 1, new DateTime(2026, 1, 1))
        };

        var result = FitnessScoring.Score(efforts);

        Assert.Single(result);
        Assert.Equal(50.0, result[0].Score);
    }

    [Fact]
    public void Score_LongSegmentOutweighsShortSegmentInAggregation()
    {
        var efforts = new[]
        {
            // Long segment (median ~1000s elapsed -> large weight): V is the better effort.
            new SegmentEffortRecord(50, 900, 100, ActivityId: 1, new DateTime(2026, 1, 1)),  // V: cost 90000, better
            new SegmentEffortRecord(50, 1100, 100, ActivityId: 2, new DateTime(2026, 1, 2)), // W: cost 110000, worse
            // Short segment (median ~50s elapsed -> small weight): V is the worse effort.
            new SegmentEffortRecord(51, 60, 160, ActivityId: 1, new DateTime(2026, 1, 1)),   // V: cost 9600, worse
            new SegmentEffortRecord(51, 40, 130, ActivityId: 3, new DateTime(2026, 1, 3))    // X: cost 5200, better
        };

        var result = FitnessScoring.Score(efforts);

        var byDate = result.ToDictionary(p => p.Date, p => p.Score);
        // Naive unweighted average of V's 100 (long) and 0 (short) would be 50.
        // The heavier long segment must dominate, pulling V's score close to 100.
        Assert.Equal(2000.0 / 21.0, byDate[new DateTime(2026, 1, 1)], precision: 6);
        Assert.True(byDate[new DateTime(2026, 1, 1)] > 90.0);
        Assert.Equal(0.0, byDate[new DateTime(2026, 1, 2)]);
        Assert.Equal(100.0, byDate[new DateTime(2026, 1, 3)]);
    }

    [Fact]
    public void Score_OutputIsSortedByWorkoutDateAscending()
    {
        var efforts = new[]
        {
            new SegmentEffortRecord(1, 200, 160, ActivityId: 1, new DateTime(2026, 1, 20)),
            new SegmentEffortRecord(1, 150, 150, ActivityId: 2, new DateTime(2026, 1, 5)),
            new SegmentEffortRecord(1, 100, 140, ActivityId: 3, new DateTime(2026, 1, 12))
        };

        var result = FitnessScoring.Score(efforts);

        Assert.Equal(
            [new DateTime(2026, 1, 5), new DateTime(2026, 1, 12), new DateTime(2026, 1, 20)],
            result.Select(p => p.Date).ToArray());
    }
}
