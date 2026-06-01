namespace FileIt.Domain.Interfaces;

/// <summary>
/// HTTP client for triggering a Databricks Workflow job from inside FileIt.
///
/// THE BIG PICTURE (read this first if you're new):
/// FileIt is the "front door." A file lands in blob storage, FileIt notices it,
/// logs that it arrived, and then needs to hand the heavy data work off to
/// Databricks (which replaced the old SSIS packages). Databricks does the ETL;
/// FileIt just kicks it off and keeps the paper trail.
///
/// This interface is that hand-off. One method: "go run that Databricks job,
/// and here's the file and the tracking id." It lives in Domain (not in the
/// module that uses it) so any module can depend on this abstraction without
/// caring HOW the call is made. The real HTTP implementation is in
/// FileIt.Infrastructure.HttpClients.DatabricksJobClient.
///
/// WHY A "CorrelationId" MATTERS:
/// It's one tracking number that follows the whole journey. FileIt stamps it
/// when the file arrives, passes it to Databricks here, and Databricks writes
/// it into its own logs. Later you can search that single id and see the entire
/// story across BOTH systems: "file arrived in FileIt -> Databricks job X ran."
/// Without it, the two systems are two separate black boxes with no thread
/// connecting them.  This is like having a primary key in a database table that links related records together.
/// It makes troubleshooting and auditing possible, because you can follow the breadcrumbs across system boundaries.
/// </summary>
public interface IDatabricksJobClient
{
    /// <summary>
    /// Tells Databricks to start a specific job RIGHT NOW (this is the
    /// Databricks "run-now" API). It does NOT wait for the job to finish; it
    /// just starts it and immediately returns the run's id. Think "press the
    /// start button and walk away," not "stand and watch until it's done."
    /// </summary>
    /// <param name="jobId">
    /// Which Databricks job to run. This is the numeric id Databricks assigns
    /// to a job when you create it (we created ours with Terraform). You can
    /// also see it in the Databricks Workflows UI on the job's page.
    /// </param>
    /// <param name="correlationId">
    /// The tracking number described above. Gets passed into the notebook so
    /// the Databricks run can be tied back to the FileIt event that triggered it.
    /// </param>
    /// <param name="filePath">
    /// Which file Databricks should process. This is the blob name/path of the
    /// file that just landed (e.g. "BIC_ActiveCustomer.txt"). The notebook reads
    /// this from blob storage.
    /// </param>
    /// <param name="cancellationToken">
    /// The standard .NET "abort if asked" signal. If the host is shutting down
    /// or the call is cancelled, the HTTP request stops cleanly instead of
    /// hanging.
    /// </param>
    /// <returns>
    /// A small result object containing the run id Databricks just created, so
    /// the caller can log it or look up the run later.
    /// </returns>
    Task<DatabricksRunResult> RunJobNowAsync(
        long jobId,
        string correlationId,
        string filePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What you get back after starting a Databricks job.
///
/// We deliberately return a tiny, plain object instead of Databricks' full
/// raw response. That keeps the messy wire details (all the fields Databricks
/// sends back) from leaking into the rest of FileIt. If Databricks changes
/// its response shape later, we only fix it in one place.
///
/// RunId:       Databricks' id for THIS specific run (one job can run many
///              times; each run gets its own id). Use it to find the run in
///              the Workflows UI or via the API.
/// NumberInJob: A human-friendly counter ("run #5 of this job"). Informational.
/// </summary>
public sealed record DatabricksRunResult(
    long RunId,
    long NumberInJob);
