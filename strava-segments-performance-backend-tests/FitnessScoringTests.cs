using StravaSegmentsPerformanceBackend.Services;

namespace strava_segments_performance_backend_tests;

public class FitnessScoringTests
{
    [Fact]
    public void Score_FasterLowerHeartRateEffort_ScoresHigherOnSharedSegment()
    {
        // Segments 1, 2, 3 share the same three activities with the same relative ranking,
        // so every workout clears the minimum-3-scored-efforts bar while keeping the same
        // per-segment costs (and therefore the same exact 0/50/100 percentiles) as a single
        // shared segment would.
        var efforts = new[]
        {
            new SegmentEffortRecord(1, 200, 160, ActivityId: 1, new DateTime(2026, 1, 3)), // slowest + highest HR -> worst
            new SegmentEffortRecord(1, 150, 150, ActivityId: 2, new DateTime(2026, 1, 1)), // middle
            new SegmentEffortRecord(1, 100, 140, ActivityId: 3, new DateTime(2026, 1, 2)), // fastest + lowest HR -> best
            new SegmentEffortRecord(2, 200, 160, ActivityId: 1, new DateTime(2026, 1, 3)),
            new SegmentEffortRecord(2, 150, 150, ActivityId: 2, new DateTime(2026, 1, 1)),
            new SegmentEffortRecord(2, 100, 140, ActivityId: 3, new DateTime(2026, 1, 2)),
            new SegmentEffortRecord(3, 200, 160, ActivityId: 1, new DateTime(2026, 1, 3)),
            new SegmentEffortRecord(3, 150, 150, ActivityId: 2, new DateTime(2026, 1, 1)),
            new SegmentEffortRecord(3, 100, 140, ActivityId: 3, new DateTime(2026, 1, 2))
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
        // A and C share segments 10, 11, 12 (3 contributing efforts each). B's only effort has
        // no HR and never enters any segment group, so it stays excluded regardless of the
        // minimum-3 rule.
        var efforts = new[]
        {
            new SegmentEffortRecord(10, 100, 150, ActivityId: 1, new DateTime(2026, 1, 1)), // A
            new SegmentEffortRecord(10, 90, null, ActivityId: 2, new DateTime(2026, 1, 2)), // B - no HR, must be dropped
            new SegmentEffortRecord(10, 95, 140, ActivityId: 3, new DateTime(2026, 1, 3)),  // C - better than A
            new SegmentEffortRecord(11, 100, 150, ActivityId: 1, new DateTime(2026, 1, 1)), // A
            new SegmentEffortRecord(11, 95, 140, ActivityId: 3, new DateTime(2026, 1, 3)),  // C
            new SegmentEffortRecord(12, 100, 150, ActivityId: 1, new DateTime(2026, 1, 1)), // A
            new SegmentEffortRecord(12, 95, 140, ActivityId: 3, new DateTime(2026, 1, 3))   // C
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
            // Segments 1, 2, 3: shared by X and Y -> 3 contributing efforts each, clearing the minimum.
            new SegmentEffortRecord(1, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // X
            new SegmentEffortRecord(1, 120, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // Y
            new SegmentEffortRecord(2, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // X
            new SegmentEffortRecord(2, 120, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // Y
            new SegmentEffortRecord(3, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // X
            new SegmentEffortRecord(3, 120, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // Y
            // Segment 99: only X ever did it -> n=1, contributes nothing, doesn't count toward the minimum either.
            new SegmentEffortRecord(99, 50, 130, ActivityId: 1, new DateTime(2026, 1, 1))
        };

        var result = FitnessScoring.Score(efforts);

        Assert.Equal(2, result.Count);
        var byDate = result.ToDictionary(p => p.Date, p => p.Score);
        // X's score matches what segments 1-3 alone would produce - segment 99 added nothing.
        Assert.Equal(100.0, byDate[new DateTime(2026, 1, 1)]);
        Assert.Equal(0.0, byDate[new DateTime(2026, 1, 2)]);
    }

    [Fact]
    public void Score_WorkoutWithNoRepeatedSegments_IsAbsentFromSeries()
    {
        var efforts = new[]
        {
            // Segments 1, 2, 3: shared by A and B -> exactly 3 contributing efforts each (the minimum).
            new SegmentEffortRecord(1, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // A
            new SegmentEffortRecord(1, 120, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // B
            new SegmentEffortRecord(2, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // A
            new SegmentEffortRecord(2, 120, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // B
            new SegmentEffortRecord(3, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // A
            new SegmentEffortRecord(3, 120, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // B
            // Segment 99: Z's only effort, unique segment -> no scorable data regardless of the minimum.
            new SegmentEffortRecord(99, 80, 120, ActivityId: 3, new DateTime(2026, 1, 3))
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
            // Segments 5, 7, 8: P vs Q, same ranking each time -> 3 contributing efforts apiece.
            new SegmentEffortRecord(5, 110, 145, ActivityId: 1, new DateTime(2026, 1, 1)), // P
            new SegmentEffortRecord(5, 100, 140, ActivityId: 2, new DateTime(2026, 1, 2)), // Q - best on segment 5
            new SegmentEffortRecord(7, 110, 145, ActivityId: 1, new DateTime(2026, 1, 1)), // P
            new SegmentEffortRecord(7, 100, 140, ActivityId: 2, new DateTime(2026, 1, 2)), // Q
            new SegmentEffortRecord(8, 110, 145, ActivityId: 1, new DateTime(2026, 1, 1)), // P
            new SegmentEffortRecord(8, 100, 140, ActivityId: 2, new DateTime(2026, 1, 2)), // Q
            // Segment 6: Q also stopped mid-segment here (elapsed >> median) -> must be dropped
            // as a stall, not scored as a terrible effort, and must not contribute its huge
            // elapsed time as aggregation weight either. S and T score normally on it.
            new SegmentEffortRecord(6, 500, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // Q's stall
            new SegmentEffortRecord(6, 100, 140, ActivityId: 3, new DateTime(2026, 1, 4)), // S
            new SegmentEffortRecord(6, 105, 140, ActivityId: 4, new DateTime(2026, 1, 5)), // T
            // Segments 9, 10: S vs T, same ranking as segment 6 -> S and T also reach 3 contributing efforts.
            new SegmentEffortRecord(9, 100, 140, ActivityId: 3, new DateTime(2026, 1, 4)),  // S
            new SegmentEffortRecord(9, 105, 140, ActivityId: 4, new DateTime(2026, 1, 5)),  // T
            new SegmentEffortRecord(10, 100, 140, ActivityId: 3, new DateTime(2026, 1, 4)), // S
            new SegmentEffortRecord(10, 105, 140, ActivityId: 4, new DateTime(2026, 1, 5))  // T
        };

        var result = FitnessScoring.Score(efforts);

        var byDate = result.ToDictionary(p => p.Date, p => p.Score);
        Assert.Equal(4, result.Count);
        // Q's stalled segment-6 effort never entered its aggregate at all - Q scores 100 purely
        // from segments 5/7/8, exactly like a workout that never touched segment 6.
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
            // Segments 20, 21, 22 repeat the same tie so M, N, O each reach 3 contributing efforts.
            new SegmentEffortRecord(20, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // M: cost 14000
            new SegmentEffortRecord(20, 140, 100, ActivityId: 2, new DateTime(2026, 1, 2)), // N: cost 14000 - tied with M
            new SegmentEffortRecord(20, 200, 150, ActivityId: 3, new DateTime(2026, 1, 3)), // O: cost 30000 - worse than both
            new SegmentEffortRecord(21, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)),
            new SegmentEffortRecord(21, 140, 100, ActivityId: 2, new DateTime(2026, 1, 2)),
            new SegmentEffortRecord(21, 200, 150, ActivityId: 3, new DateTime(2026, 1, 3)),
            new SegmentEffortRecord(22, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)),
            new SegmentEffortRecord(22, 140, 100, ActivityId: 2, new DateTime(2026, 1, 2)),
            new SegmentEffortRecord(22, 200, 150, ActivityId: 3, new DateTime(2026, 1, 3))
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
        // Same activity visits the same segment three times within one ride - the only workout
        // in the window, so min == max and the degenerate-window rule applies.
        var efforts = new[]
        {
            new SegmentEffortRecord(40, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)),
            new SegmentEffortRecord(40, 120, 150, ActivityId: 1, new DateTime(2026, 1, 1)),
            new SegmentEffortRecord(40, 110, 145, ActivityId: 1, new DateTime(2026, 1, 1))
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
            new SegmentEffortRecord(51, 40, 130, ActivityId: 3, new DateTime(2026, 1, 3)),   // X: cost 5200, better
            // Second short segment: V is the worse effort again here too.
            new SegmentEffortRecord(53, 45, 150, ActivityId: 1, new DateTime(2026, 1, 1)),   // V: cost 6750, worse
            new SegmentEffortRecord(53, 35, 120, ActivityId: 4, new DateTime(2026, 1, 4)),   // Y: cost 4200, better
            // W's other two contributing segments - always the worse effort, so W ends at 0.
            new SegmentEffortRecord(54, 110, 150, ActivityId: 2, new DateTime(2026, 1, 2)),  // W: cost 16500, worse
            new SegmentEffortRecord(54, 100, 140, ActivityId: 5, new DateTime(2026, 1, 5)),  // Z: cost 14000, better
            new SegmentEffortRecord(55, 110, 150, ActivityId: 2, new DateTime(2026, 1, 2)),  // W: cost 16500, worse
            new SegmentEffortRecord(55, 100, 140, ActivityId: 5, new DateTime(2026, 1, 5)),  // Z: cost 14000, better
            // X's other two contributing segments - always the better effort, so X ends at 100.
            new SegmentEffortRecord(56, 40, 130, ActivityId: 3, new DateTime(2026, 1, 3)),   // X: cost 5200, better
            new SegmentEffortRecord(56, 60, 160, ActivityId: 6, new DateTime(2026, 1, 6)),   // Q: cost 9600, worse
            new SegmentEffortRecord(57, 40, 130, ActivityId: 3, new DateTime(2026, 1, 3)),   // X: cost 5200, better
            new SegmentEffortRecord(57, 60, 160, ActivityId: 6, new DateTime(2026, 1, 6))    // Q: cost 9600, worse
        };

        var result = FitnessScoring.Score(efforts);

        var byDate = result.ToDictionary(p => p.Date, p => p.Score);
        // V: (1000*100 + 50*0 + 40*0) / 1090 = 10000/109 ~= 91.74. A naive unweighted average of
        // V's three percentiles (100, 0, 0) would be ~33.3 - the heavy long segment must dominate
        // instead, pulling V's score close to (but below) 100.
        Assert.Equal(10000.0 / 109.0, byDate[new DateTime(2026, 1, 1)], precision: 6);
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
            new SegmentEffortRecord(1, 100, 140, ActivityId: 3, new DateTime(2026, 1, 12)),
            new SegmentEffortRecord(2, 200, 160, ActivityId: 1, new DateTime(2026, 1, 20)),
            new SegmentEffortRecord(2, 150, 150, ActivityId: 2, new DateTime(2026, 1, 5)),
            new SegmentEffortRecord(2, 100, 140, ActivityId: 3, new DateTime(2026, 1, 12)),
            new SegmentEffortRecord(3, 200, 160, ActivityId: 1, new DateTime(2026, 1, 20)),
            new SegmentEffortRecord(3, 150, 150, ActivityId: 2, new DateTime(2026, 1, 5)),
            new SegmentEffortRecord(3, 100, 140, ActivityId: 3, new DateTime(2026, 1, 12))
        };

        var result = FitnessScoring.Score(efforts);

        Assert.Equal(
            [new DateTime(2026, 1, 5), new DateTime(2026, 1, 12), new DateTime(2026, 1, 20)],
            result.Select(p => p.Date).ToArray());
    }

    [Fact]
    public void Score_WorkoutWithFewerThanThreeScoredEfforts_IsExcludedFromSeries()
    {
        // A shares 2 segments with B and 3 segments with C, so A independently accumulates
        // 5 contributing efforts (well above the minimum) and is scored normally. B shares
        // only those same 2 segments and never reaches the minimum of 3 on its own, so B is
        // excluded - the exclusion is per-workout, not contagious to A.
        var efforts = new[]
        {
            new SegmentEffortRecord(1, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // A vs B
            new SegmentEffortRecord(1, 120, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // B
            new SegmentEffortRecord(2, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // A vs B
            new SegmentEffortRecord(2, 120, 150, ActivityId: 2, new DateTime(2026, 1, 2)), // B
            new SegmentEffortRecord(3, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // A vs C
            new SegmentEffortRecord(3, 120, 150, ActivityId: 3, new DateTime(2026, 1, 3)), // C
            new SegmentEffortRecord(4, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // A vs C
            new SegmentEffortRecord(4, 120, 150, ActivityId: 3, new DateTime(2026, 1, 3)), // C
            new SegmentEffortRecord(5, 100, 140, ActivityId: 1, new DateTime(2026, 1, 1)), // A vs C
            new SegmentEffortRecord(5, 120, 150, ActivityId: 3, new DateTime(2026, 1, 3))  // C
        };

        var result = FitnessScoring.Score(efforts);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, p => p.Date == new DateTime(2026, 1, 2)); // B: only 2 contributing efforts
        var byDate = result.ToDictionary(p => p.Date, p => p.Score);
        Assert.Equal(100.0, byDate[new DateTime(2026, 1, 1)]); // A: scored from its 3 segments with C
        Assert.Equal(0.0, byDate[new DateTime(2026, 1, 3)]);   // C
    }
}
