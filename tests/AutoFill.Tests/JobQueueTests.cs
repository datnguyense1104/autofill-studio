using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Data;
using AutoFill.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoFill.Tests;

[Collection(PostgresCollection.Name)]
public sealed class JobQueueTests(PostgresServer pg) : IClassFixture<PostgresServer>, IAsyncLifetime
{
    TestApp app = null!;

    sealed class EchoHandler : IJobHandler
    {
        public string Type => "echo";
        public async Task<string?> RunAsync(Job job, IJobProgress progress, CancellationToken ct)
        {
            await progress.ReportAsync("Đang chạy", 1, 2);
            return job.Payload;
        }
    }

    sealed class BrokenHandler : IJobHandler
    {
        public string Type => "broken";
        public Task<string?> RunAsync(Job job, IJobProgress progress, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    sealed class BadInputHandler : IJobHandler
    {
        public string Type => "bad-input";
        public Task<string?> RunAsync(Job job, IJobProgress progress, CancellationToken ct) => throw new UserFacingException("File hỏng");
    }

    public async Task InitializeAsync() => app = new TestApp(await pg.CreateDatabaseAsync())
    {
        TestServices = s =>
        {
            s.AddScoped<IJobHandler, EchoHandler>();
            s.AddScoped<IJobHandler, BrokenHandler>();
            s.AddScoped<IJobHandler, BadInputHandler>();
            s.Replace(ServiceDescriptor.Singleton(new JobQueueOptions(TimeSpan.FromMinutes(10), MaxAttempts: 3, RetryDelay: TimeSpan.Zero)));
        },
    };

    public async Task DisposeAsync() => await app.DisposeAsync();

    async Task<Guid> EnqueueAsync(string type, object payload)
    {
        await using var scope = await app.WorkspaceScopeAsync("a@example.com");
        return (await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueAsync(type, payload, null)).Id;
    }

    async Task<Job> ReadAsync(Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == id);
    }

    JobWorker Worker => app.Services.GetRequiredService<JobWorker>();

    [Fact]
    public async Task A_job_runs_once_and_its_result_reaches_the_owner()
    {
        var client = await app.SignedInAsync("a@example.com");
        var id = await EnqueueAsync("echo", new { hello = "xin chào" });

        Assert.True(await Worker.RunOneAsync(CancellationToken.None));
        Assert.False(await Worker.RunOneAsync(CancellationToken.None));

        var job = await client.GetFromJsonAsync<JsonObject>($"/api/jobs/{id}");
        Assert.Equal("done", job!["status"]!.GetValue<string>());
        Assert.Equal("xin chào", job["result"]!["hello"]!.GetValue<string>());
        Assert.Equal("Đang chạy", job["progress"]!["step"]!.GetValue<string>());

        // The event stream sends the final state and closes.
        var events = await client.GetStringAsync($"/api/jobs/{id}/events");
        Assert.StartsWith("data: ", events);
        Assert.Contains("\"status\":\"done\"", events);
    }

    [Fact]
    public async Task Two_workers_never_take_the_same_job()
    {
        await app.SignedInAsync("a@example.com");
        await EnqueueAsync("echo", new { n = 1 });

        async Task<Job?> ClaimAsync()
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<JobQueue>().ClaimAsync();
        }
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ClaimAsync()));
        Assert.Single(claims, c => c is not null);
    }

    [Fact]
    public async Task A_failing_job_is_retried_then_given_up()
    {
        await app.SignedInAsync("a@example.com");
        var id = await EnqueueAsync("broken", new { });

        for (var i = 1; i <= 3; i++)
        {
            Assert.True(await Worker.RunOneAsync(CancellationToken.None));
            var job = await ReadAsync(id);
            Assert.Equal(i, job.Attempts);
            Assert.Equal(i < 3 ? JobStatus.Queued : JobStatus.Failed, job.Status);
        }
        Assert.False(await Worker.RunOneAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Bad_input_and_unknown_job_types_fail_without_retry()
    {
        await app.SignedInAsync("a@example.com");
        var bad = await EnqueueAsync("bad-input", new { });
        var unknown = await EnqueueAsync("no-such-type", new { });

        Assert.True(await Worker.RunOneAsync(CancellationToken.None));
        Assert.True(await Worker.RunOneAsync(CancellationToken.None));
        Assert.False(await Worker.RunOneAsync(CancellationToken.None));

        Assert.Equal(("File hỏng", JobStatus.Failed), ((await ReadAsync(bad)).Error, (await ReadAsync(bad)).Status));
        Assert.Equal(JobStatus.Failed, (await ReadAsync(unknown)).Status);
    }
}
