using FileIt.Domain.Entities.DeadLetter;
using FileIt.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.Ui.Host;

public class LogsApi
{
    private readonly IDbContextFactory<CommonDbContext> _factory;
    private readonly ILogger<LogsApi> _logger;

    public LogsApi(IDbContextFactory<CommonDbContext> factory, ILogger<LogsApi> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // List the most recent N function-start events across all apps, EventId=2 marks the
    // start of an invocation in the MiddlewareLogger convention from #41. The list view
    // is what the operator inbox lands on.
    [Function("GetRecentFlows")]
    public async Task<IActionResult> GetRecentFlows(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "flows")] HttpRequest req,
        CancellationToken ct)
    {
        var take = int.TryParse(req.Query["take"], out var t) ? Math.Min(t, 200) : 50;
        await using var db = _factory.CreateDbContext();

        // One row per CorrelationId, ordered by most recent. Filter by last 24h
        // so the query is fast even with millions of historical rows.
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var flows = await db.CommonLogs
            .AsNoTracking()
            .Where(l => l.CreatedOn >= cutoff
                && l.CorrelationId != null
                && l.CorrelationId != "")
            .OrderByDescending(l => l.Id)
            .Take(take * 10)  // overshoot to allow distinct-by-CorrelationId
            .ToListAsync(ct);

        var distinct = flows
            .GroupBy(l => l.CorrelationId)
            .Select(g => g.OrderBy(x => x.Id).First())
            .OrderByDescending(x => x.CreatedOn)
            .Take(take)
            .Select(l => new
            {
                l.Id,
                l.Application,
                l.InvocationId,
                l.CorrelationId,
                l.SourceContext,
                l.CreatedOn,
                l.Level
            })
            .ToList();

        return new OkObjectResult(distinct);
    }

    // Drill-down: every log row for one CorrelationId, ordered by CreatedOn ascending.
    // The flow visualization view builds its pipeline animation from this list.
    [Function("GetFlowTimeline")]
    public async Task<IActionResult> GetFlowTimeline(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "flows/{id}")] HttpRequest req,
        string id,
        CancellationToken ct)
    {
        await using var db = _factory.CreateDbContext();
        // Try CorrelationId first (the normal case). If empty, fall back to InvocationId
        // because Recent flows uses InvocationId when CorrelationId is null on the source row.
        var rows = await db.CommonLogs
            .AsNoTracking()
            .Where(l => l.CorrelationId == id)
            .OrderBy(l => l.CreatedOn)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            rows = await db.CommonLogs
                .AsNoTracking()
                .Where(l => l.InvocationId == id)
                .OrderBy(l => l.CreatedOn)
                .ToListAsync(ct);
        }
        return new OkObjectResult(rows);
    }

    // Sibling drill-down by InvocationId when CorrelationId is empty (e.g. the
    // MiddlewareLogger row that has only InvocationId stamped).
    [Function("GetFlowByInvocation")]
    public async Task<IActionResult> GetFlowByInvocation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "invocations/{invocationId}")] HttpRequest req,
        string invocationId,
        CancellationToken ct)
    {
        await using var db = _factory.CreateDbContext();
        var rows = await db.CommonLogs
            .AsNoTracking()
            .Where(l => l.InvocationId == invocationId)
            .OrderBy(l => l.CreatedOn)
            .ToListAsync(ct);
        return new OkObjectResult(rows);
    }

    // Dead-letter inbox, most recent first. Powers the DLQ pane and the poison-CSV demo.
    [Function("GetDeadLetters")]
    public async Task<IActionResult> GetDeadLetters(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "deadletters")] HttpRequest req,
        CancellationToken ct)
    {
        var take = int.TryParse(req.Query["take"], out var t) ? Math.Min(t, 200) : 50;
        await using var db = _factory.CreateDbContext();
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var rows = await db.DeadLetterRecords
            .AsNoTracking()
            .Where(r => r.DeadLetteredTimeUtc >= cutoff)
            .OrderByDescending(r => r.DeadLetterRecordId)
            .Take(take)
            .ToListAsync(ct);
        return new OkObjectResult(rows);
    }
}
