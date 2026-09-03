using Microsoft.EntityFrameworkCore;
using StravaSegmentsPerformanceBackend.Data;
using StravaSegmentsPerformanceBackend.Models;

namespace StravaSegmentsPerformanceBackend.Services;

public class WorkoutFetchWorker : BackgroundService
{
    private const int PageSize = 50;
    private static readonly TimeSpan WholeFetchTimeout = TimeSpan.FromHours(3);

    private readonly WorkoutFetchChannel _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkoutFetchWorker> _logger;

    public WorkoutFetchWorker(WorkoutFetchChannel channel, IServiceScopeFactory scopeFactory, ILogger<WorkoutFetchWorker> logger)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            using var opCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            opCts.CancelAfter(WholeFetchTimeout);
            try
            {
                await ProcessUserAsync(request, opCts.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // App is shutting down — leave the status for the startup reset to recover.
                throw;
            }
            catch (OperationCanceledException) when (opCts.IsCancellationRequested)
            {
                _logger.LogError("Workout fetch for user {UserId} exceeded {Timeout} and was aborted", request.UserId, WholeFetchTimeout);
                await MarkFailedAsync(request.UserId, $"Fetch exceeded the {WholeFetchTimeout.TotalHours}h limit and was aborted.", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workout fetch failed for user {UserId}", request.UserId);
                await MarkFailedAsync(request.UserId, ex.Message, stoppingToken);
            }
        }
    }

    private async Task ProcessUserAsync(FetchRequest request, CancellationToken ct)
    {
        var userId = request.UserId;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stravaClient = scope.ServiceProvider.GetRequiredService<StravaApiClient>();

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        var status = await db.WorkoutFetchStatuses.FirstAsync(s => s.UserId == userId, ct);

        status.Status = FetchStatusState.Running;
        status.Stage = FetchStage.ListingActivities;
        await db.SaveChangesAsync(ct);

        var existingActivityIds = (await db.Activities
            .Where(a => a.UserId == userId)
            .Select(a => a.StravaActivityId)
            .ToListAsync(ct))
            .ToHashSet();

        var page = 1;
        int discovered;
        while (true)
        {
            var summaries = await stravaClient.ListActivitiesPageAsync(user, page, PageSize, request.AfterUtc, request.BeforeUtc, ct);
            if (summaries.Count == 0)
                break;

            foreach (var summary in summaries)
            {
                if (!summary.IsRelevantCyclingActivity())
                    continue;
                if (existingActivityIds.Contains(summary.Id))
                    continue;

                db.Activities.Add(summary.ToActivity(userId));
                existingActivityIds.Add(summary.Id);
            }

            discovered = await db.Activities.CountAsync(a => a.UserId == userId, ct);
            status.ActivitiesProcessed = discovered;
            await db.SaveChangesAsync(ct);

            if (summaries.Count < PageSize)
                break;

            page++;
        }

        status.Stage = FetchStage.FetchingDetails;
        status.ActivitiesProcessed = 0;
        status.TotalToProcess = await db.Activities.CountAsync(a => a.UserId == userId && !a.DetailsFetched, ct);
        await db.SaveChangesAsync(ct);

        var pending = await db.Activities
            .Where(a => a.UserId == userId && !a.DetailsFetched)
            .ToListAsync(ct);

        foreach (var activity in pending)
        {
            var detail = await stravaClient.GetActivityDetailAsync(user, activity.StravaActivityId, ct);
            db.SegmentEfforts.AddRange(detail.ToSegmentEfforts(activity.Id));
            activity.DetailsFetched = true;

            status.ActivitiesProcessed++;
            await db.SaveChangesAsync(ct);

            if(status.ActivitiesProcessed == 10)
                break;
        }

        status.Status = FetchStatusState.Completed;
        status.CompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkFailedAsync(int userId, string errorMessage, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var status = await db.WorkoutFetchStatuses.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (status is null)
            return;

        status.Status = FetchStatusState.Failed;
        status.ErrorMessage = errorMessage;
        await db.SaveChangesAsync(ct);
    }
}
