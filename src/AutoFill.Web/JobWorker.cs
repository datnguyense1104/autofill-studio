using System.Text.Json;
using AutoFill.Ai;
using AutoFill.Core;
using AutoFill.Data;

namespace AutoFill.Web;

/// <summary>
/// Runs queued jobs in this process: <see cref="Settings.Workers"/> loops, each taking one job at
/// a time. Each job runs in its own DI scope, inside the job's workspace.
/// </summary>
public sealed class JobWorker(IServiceScopeFactory scopes, Settings settings, ILogger<JobWorker> log) : BackgroundService
{
    static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);

    protected override Task ExecuteAsync(CancellationToken stop) =>
        Task.WhenAll(Enumerable.Range(0, settings.Workers).Select(_ => LoopAsync(stop)));

    async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!await RunOneAsync(stop)) await Task.Delay(Idle, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception err)
            {
                // Database unreachable and the like: wait, then try again.
                log.LogError(err, "Job loop error");
                await Task.Delay(TimeSpan.FromSeconds(10), stop);
            }
        }
    }

    /// <returns>false when the queue was empty</returns>
    public async Task<bool> RunOneAsync(CancellationToken stop)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        var job = await queue.ClaimAsync(stop);
        if (job is null) return false;

        var workspace = (WorkspaceScope)scope.ServiceProvider.GetRequiredService<IWorkspaceScope>();
        workspace.WorkspaceId = job.WorkspaceId;
        workspace.JobId = job.Id;
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
        if (handler is null)
        {
            await queue.FailAsync(job, $"Không có bộ xử lý cho loại việc '{job.Type}'", retry: false, CancellationToken.None);
            return true;
        }

        log.LogInformation("Job {Id} ({Type}) attempt {Attempt}", job.Id, job.Type, job.Attempts);
        try
        {
            var result = await handler.RunAsync(job, new Progress(queue, job.Id), stop);
            await queue.CompleteAsync(job.Id, result, CancellationToken.None);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Shutting down: the lease runs out and another worker picks the job up again.
            throw;
        }
        catch (UserFacingException err)
        {
            // A problem with the input (bad file...): retrying would give the same answer.
            await queue.FailAsync(job, err.Message, retry: false, CancellationToken.None);
        }
        catch (Exception err)
        {
            log.LogError(err, "Job {Id} ({Type}) failed", job.Id, job.Type);
            await queue.FailAsync(job, "Lỗi khi xử lý, hệ thống sẽ thử lại", retry: true, CancellationToken.None);
        }
        return true;
    }

    sealed class Progress(JobQueue queue, Guid jobId) : IJobProgress
    {
        public Task ReportAsync(string step, int? done = null, int? total = null) =>
            queue.ReportProgressAsync(jobId, JsonSerializer.Serialize(new { step, done, total }));
    }
}

/// <summary>AI usage rows go through their own database context, so they never save a caller's half-done changes.</summary>
public sealed class AiUsageLog(IServiceScopeFactory scopes, ILogger<AiUsageLog> log) : IAiUsageLog
{
    public async Task WriteAsync(AiLog entry, CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiLogs.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception err)
        {
            log.LogWarning(err, "Could not record AI usage");
        }
    }
}
