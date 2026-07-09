using Microsoft.EntityFrameworkCore;
using StravaSegmentsPerformanceBackend.Models;

namespace StravaSegmentsPerformanceBackend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<SegmentEffort> SegmentEfforts => Set<SegmentEffort>();
    public DbSet<WorkoutFetchStatus> WorkoutFetchStatuses => Set<WorkoutFetchStatus>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.StravaAthleteId)
            .IsUnique();

        modelBuilder.Entity<Activity>()
            .HasIndex(a => new { a.UserId, a.StravaActivityId })
            .IsUnique();

        modelBuilder.Entity<SegmentEffort>()
            .HasIndex(e => e.StravaSegmentEffortId)
            .IsUnique();

        modelBuilder.Entity<WorkoutFetchStatus>()
            .HasKey(s => s.UserId);

        modelBuilder.Entity<WorkoutFetchStatus>()
            .Property(s => s.UserId)
            .ValueGeneratedNever();
    }
}
