// HTTP-triggered per-record replay function. Operators (or the future FileIt UI)
// hit POST /api/deadletter/{id}/replay to promote a single record from PendingReplay
// to Replayed by force.
//
// All replay logic lives in IDeadLetterReplayService. This function is a thin HTTP
// adapter: it parses the route id, names the initiator from the request, calls
// the service, and translates the outcome to an HTTP status code.
//
// See docs/dead-letter-strategy.md for the full design.
using System.Net;
using FileIt.Infrastructure;
using FileIt.Infrastructure.DeadLetter.Replay;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.Services.Host;

/// <summary>
/// Azure Function exposing operator-driven per-record replay over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Route: <c>POST /api/deadletter/{id:long}/replay</c>. The numeric id matches
/// <see cref="FileIt.Domain.Entities.DeadLetter.DeadLetterRecord.DeadLetterRecordId"/>.
/// </para>
/// <para>
/// Authorization is currently <see cref="AuthorizationLevel.Function"/> so every
/// invocation requires a function key. When the FileIt UI lands (see issue #17),
/// the UI will call through a backing-for-frontend that supplies the key; direct
/// curl from operator workstations also works as long as the key is set in the
/// <c>x-functions-key</c> header.
/// </para>
/// <para>
/// Outcome-to-status mapping mirrors REST conventions:
/// <list type="bullet">
///   <item><description><see cref="DeadLetterReplayResult.Sent"/> -> 200 OK with the outcome body.</description></item>
///   <item><description><see cref="DeadLetterReplayResult.NotFound"/> -> 404 Not Found.</description></item>
///   <item><description><see cref="DeadLetterReplayResult.NotEligible"/> -> 409 Conflict (resource exists but not in a state that admits the operation).</description></item>
///   <item><description><see cref="DeadLetterReplayResult.InvalidRecord"/> -> 422 Unprocessable Entity (well-formed request, semantically un-actionable).</description></item>
///   <item><description><see cref="DeadLetterReplayResult.SendFailed"/> -> 502 Bad Gateway (the broker rejected our send, downstream failure).</description></item>
/// </list>
/// </para>
/// </remarks>
public class DeadLetterReplayHttp
{
    public const string FunctionName = nameof(DeadLetterReplayHttp);
    public const string Route = "deadletter/{id:long}/replay";

    /// <summary>
    /// Default initiator label when the caller does not supply one in the
    /// <c>X-Initiated-By</c> header. Lands in <c>DeadLetterRecord.StatusUpdatedBy</c>
    /// for audit; operators are encouraged but not required to identify themselves.
    /// </summary>
    public const string DefaultInitiatedBy = "operator";

    private readonly IDeadLetterReplayService _replay;
    private readonly ILogger<DeadLetterReplayHttp> _logger;

    public DeadLetterReplayHttp(
        IDeadLetterReplayService replay,
        ILogger<DeadLetterReplayHttp> logger)
    {
        _replay = replay ?? throw new ArgumentNullException(nameof(replay));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [Function(FunctionName)]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = Route)]
            HttpRequestData request,
        long id,
        FunctionContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cancellationToken = context.CancellationToken;

        var initiatedBy = ResolveInitiatedBy(request);

        _logger.LogInformation(
            InfrastructureEvents.ReplayInitiated,
            "{FunctionName} invoked: DeadLetterRecordId={DeadLetterRecordId}, "
                + "InitiatedBy={InitiatedBy}.",
            FunctionName,
            id,
            initiatedBy);

        var outcome = await _replay.ReplayAsync(id, initiatedBy, cancellationToken)
            .ConfigureAwait(false);

        var statusCode = MapOutcomeToStatus(outcome.Result);
        var response = request.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(outcome, cancellationToken)
            .ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Reads the <c>X-Initiated-By</c> header; falls back to <see cref="DefaultInitiatedBy"/>
    /// when absent or blank. The header is advisory, not authoritative, since
    /// function-key auth does not identify an actor.
    /// </summary>
    private static string ResolveInitiatedBy(HttpRequestData request)
    {
        if (request.Headers.TryGetValues("X-Initiated-By", out var values))
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v.Trim();
                }
            }
        }
        return DefaultInitiatedBy;
    }

    private static HttpStatusCode MapOutcomeToStatus(DeadLetterReplayResult result)
    {
        return result switch
        {
            DeadLetterReplayResult.Sent => HttpStatusCode.OK,
            DeadLetterReplayResult.NotFound => HttpStatusCode.NotFound,
            DeadLetterReplayResult.NotEligible => HttpStatusCode.Conflict,
            DeadLetterReplayResult.InvalidRecord => HttpStatusCode.UnprocessableEntity,
            DeadLetterReplayResult.SendFailed => HttpStatusCode.BadGateway,
            _ => HttpStatusCode.InternalServerError,
        };
    }
}