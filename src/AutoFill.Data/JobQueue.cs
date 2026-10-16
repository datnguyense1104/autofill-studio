using System.Text.Json;
using AutoFill.Core;
using Microsoft.EntityFrameworkCore;

namespace AutoFill.Data;

public sealed record JobQueueOptions(TimeSpan Lease, int MaxAttempts, TimeSpan RetryDelay);

/// <summary>
/// Background jobs kept in the <c>jobs</c> table. Workers claim one job at a time with
/// <c>FOR UPDATE SKIP LOCKED</c>, so two workers (or two containers) never take the same job.
/// A claimed job holds a lease; a worker that dies lets the lease run out and the job is retried.
/// </summary>
public sealed class JobQueue(AppDbContext db, JobQueueOptions options)
{
    /// <summary>Queues a job in the current workspace.</summary>
    public async Task<Job> EnqueueAsync(string type, object payload, Guid? createdBy, CancellationToken ct = default)
    {
        var job = new Job { Type = type, Payload = JsonSerializer.Serialize(payload), CreatedBy = createdBy };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    /// <summary>Takes the oldest job that is waiting (or whose worker stopped answering); null when there is none.</summary>
    public async Task<Job?> ClaimAsync(CancellationToken ct = default)
    {
        var lease = options.Lease.TotalSeconds;
        // Jobs whose worker vanished too many times are given up.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs SET status = 'Failed', error = 'Quá thời gian xử lý', locked_until = NULL, updated_at = now()
            WHERE status = 'Running' AND locked_until < now() AND attempts >= {options.MaxAttempts}
            """, ct);
        var claimed = await db.Jobs.FromSqlInterpolated($"""
            UPDATE jobs SET status = 'Running', attempts = attempts + 1,
                   locked_until = now() + make_interval(secs => {lease}), updated_at = now()
            WHERE id = (
                SELECT id FROM jobs
                WHERE (status = 'Queued' AND (locked_until IS NULL OR locked_until <= now()))
                   OR (status = 'Running' AND locked_until < now())
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING *
            """).IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        return claimed.FirstOrDefault();
    }

    /// <summary>Records progress and extends the lease (a job that reports progress is alive).</summary>
    public Task ReportProgressAsync(Guid jobId, string progressJson, CancellationToken ct = default) =>
        db.Jobs.IgnoreQueryFilters().Where(j => j.Id == jobId && j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Progress, progressJson)
                .SetProperty(j => j.LockedUntil, DateTimeOffset.UtcNow + options.Lease)
                .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);

    public Task CompleteAsync(Guid jobId, string? resultJson, CancellationToken ct = default) =>
        db.Jobs.IgnoreQueryFilters().Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Done)
                .SetProperty(j => j.Result, resultJson)
                .SetProperty(j => j.Error, (string?)null)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);

    /// <summary>Puts the job back in the queue (after a delay) until it has used all its attempts.</summary>
    public async Task FailAsync(Job job, string error, bool retry, CancellationToken ct = default)
    {
        var again = retry && job.Attempts < options.MaxAttempts;
        await db.Jobs.IgnoreQueryFilters().Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, again ? JobStatus.Queued : JobStatus.Failed)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.LockedUntil, again ? DateTimeOffset.UtcNow + options.RetryDelay * job.Attempts : null)
                .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }
}
