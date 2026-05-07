// This is where the actual work happens.
// We take the raw GL Account CSV file, parse every row,
// group the accounts by company code and account group,
// count how many accounts are in each group,
// and produce a clean summary CSV as the output.
// No Azure resources, no SSIS, pure C#.
using FileIt.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.DataFlow.App.Transform;

public interface ITransformGlAccounts
{
    Task<string> RunAsync(Stream csvStream, string correlationId, CancellationToken cancellationToken = default);
}

public class TransformGlAccounts : ITransformGlAccounts
{
    /// <summary>
    /// Sentinel prefix that, when found at the start of any GL Account COMPANYCODE
    /// value, causes the transform to throw a deterministic exception. Used by the
    /// dead-letter pipeline to demonstrate end-to-end DLQ handling: a CSV containing
    /// this prefix fails processing five times, gets dead-lettered by Service Bus,
    /// is picked up by DataFlowDeadLetterReader, classified as Poison by the rule
    /// in DeadLetterClassifier.PoisonPayloadPrefix, and surfaces in dbo.DeadLetterRecord
    /// for operator review.
    /// </summary>
    /// <remarks>
    /// The string value is intentionally aligned with
    /// <c>DeadLetterClassifier.PoisonPayloadPrefix</c>; do not change one without
    /// the other. See docs/dead-letter-strategy.md Section 10 for the full demo
    /// procedure.
    /// </remarks>
    public const string PoisonCompanyCodePrefix = "POISON_";

    private readonly ILogger<TransformGlAccounts> _logger;

    public TransformGlAccounts(ILogger<TransformGlAccounts> logger)
    {
        _logger = logger;
    }

    public async Task<string> RunAsync(Stream csvStream, string correlationId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            DataFlowEvents.DataFlowTransform,
            "Starting GL Account transform for correlation {CorrelationId}",
            correlationId
        );

        // Read all the lines from the CSV stream
        var lines = new List<string>();
        using var reader = new StreamReader(csvStream);
        string? csvLine;
        while ((csvLine = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add(csvLine);
        }

        // First line is the header – skip it but use it to find our column positions
        var headers = lines[0].Split(',');
        int companyCodeIndex = Array.IndexOf(headers, " COMPANYCODE");
        int accountGroupIndex = Array.IndexOf(headers, " GLACCOUNTGROUP");
        int balanceSheetIndex = Array.IndexOf(headers, " ISBALANCESHEETACCOUNT");

        // === FIX: Validate that all required columns exist in the CSV header ===
        // Array.IndexOf returns -1 if not found; accessing fields[<negative index>] later
        // would throw IndexOutOfRangeException. Fail fast with a clear message.
        if (companyCodeIndex == -1)
        {
            throw new InvalidOperationException(
                $"Required CSV column ' COMPANYCODE' not found in header. " +
                $"Available columns: {string.Join(", ", headers)}. " +
                $"Correlation {correlationId}.");
        }
        if (accountGroupIndex == -1)
        {
            throw new InvalidOperationException(
                $"Required CSV column ' GLACCOUNTGROUP' not found in header. " +
                $"Available columns: {string.Join(", ", headers)}. " +
                $"Correlation {correlationId}.");
        }
        if (balanceSheetIndex == -1)
        {
            throw new InvalidOperationException(
                $"Required CSV column ' ISBALANCESHEETACCOUNT' not found in header. " +
                $"Available columns: {string.Join(", ", headers)}. " +
                $"Correlation {correlationId}.");
        }
        // === END FIX ===

        // Group the rows by company code and account group and count them
        // We use a dictionary where the key is "COMPANYCODE|GLACCOUNTGROUP"
        var summary = new Dictionary<string, (int Count, int BalanceSheetCount)>();

        foreach (var row in lines.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fields = row.Split(',');

            if (fields.Length <= Math.Max(companyCodeIndex, accountGroupIndex))
                continue;

            string companyCode = fields[companyCodeIndex].Trim();
            string accountGroup = fields[accountGroupIndex].Trim();
            string isBalanceSheet = fields[balanceSheetIndex].Trim();

            // Deliberate poison trigger for the dead-letter demo. Any row whose
            // COMPANYCODE begins with PoisonCompanyCodePrefix causes the transform
            // to throw a deterministic exception. The exception message embeds the
            // same prefix so the dead-letter classifier categorizes the resulting
            // DLQ record as Poison via its PoisonPayloadPrefix rule.
            if (companyCode.StartsWith(PoisonCompanyCodePrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Deliberate poison trigger fired: COMPANYCODE '{companyCode}' starts "
                    + $"with '{PoisonCompanyCodePrefix}'. This row is a poison test marker; "
                    + "see docs/dead-letter-strategy.md Section 10. Correlation "
                    + $"{correlationId}.");
            }

            string key = $"{companyCode}|{accountGroup}";

            if (!summary.ContainsKey(key))
                summary[key] = (0, 0);

            var current = summary[key];
            int balanceSheetIncrement = isBalanceSheet == "X" ? 1 : 0;
            summary[key] = (current.Count + 1, current.BalanceSheetCount + balanceSheetIncrement);
        }

        // Build the output CSV
        var outputLines = new List<string>
        {
            "CompanyCode,AccountGroup,TotalAccounts,BalanceSheetAccounts,NonBalanceSheetAccounts"
        };

        foreach (var kvp in summary.OrderBy(k => k.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parts = kvp.Key.Split('|');
            string companyCode = parts[0];
            string accountGroup = parts[1];
            int total = kvp.Value.Count;
            int balanceSheet = kvp.Value.BalanceSheetCount;
            int nonBalanceSheet = total - balanceSheet;

            outputLines.Add($"{companyCode},{accountGroup},{total},{balanceSheet},{nonBalanceSheet}");
        }

        _logger.LogInformation(
            DataFlowEvents.DataFlowTransformCompleted,
            "GL Account transform complete. {GroupCount} groups produced for correlation {CorrelationId}",
            summary.Count,
            correlationId
        );

        return string.Join(Environment.NewLine, outputLines);
    }
}
