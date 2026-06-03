using System.Text;

namespace FileIt.Module.Ui.Host;

public enum ColumnType
{
    Text,
    TextRequired,
    Integer,
    Decimal,
    Date,
    FlagXorNull,
    AnythingGoes
}

public record ColumnSpec(string Name, ColumnType Type, bool AllowEmpty = true);

public class ValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = new();
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public string? Stage { get; init; }
}

public static class FileItValidator
{
    private const int MaxErrorsReported = 20;

    private static readonly Dictionary<string, (int Columns, char Delimiter)> ModuleSchemas = new()
    {
        ["dataflow"]   = (30, ','),
        ["salesforce"] = (40, '\t'),
    };

    private static readonly ColumnSpec[] GLAccountSchema = new[]
    {
        new ColumnSpec("GLAccountKey", ColumnType.Integer, AllowEmpty: false),
        new ColumnSpec("FileImportLogKey", ColumnType.Integer, AllowEmpty: false),
        new ColumnSpec("ImportDateTime", ColumnType.Date, AllowEmpty: false),
        new ColumnSpec("GLACCOUNT", ColumnType.TextRequired),
        new ColumnSpec("COMPANYCODE", ColumnType.TextRequired),
        new ColumnSpec("CHARTOFACCOUNTS", ColumnType.Text),
        new ColumnSpec("GLACCOUNTGROUP", ColumnType.Text),
        new ColumnSpec("CORPORATEGROUPACCOUNT", ColumnType.Text),
        new ColumnSpec("ACCOUNTISBLOCKEDFORPOSTING", ColumnType.FlagXorNull),
        new ColumnSpec("ACCOUNTISBLOCKEDFORPLANNING", ColumnType.FlagXorNull),
        new ColumnSpec("ACCOUNTISBLOCKEDFORCREATION", ColumnType.FlagXorNull),
        new ColumnSpec("ISBALANCESHEETACCOUNT", ColumnType.FlagXorNull),
        new ColumnSpec("ACCOUNTISMARKEDFORDELETION", ColumnType.FlagXorNull),
        new ColumnSpec("PARTNERCOMPANY", ColumnType.Text),
        new ColumnSpec("FUNCTIONALAREA", ColumnType.Text),
        new ColumnSpec("CREATIONDATE", ColumnType.Text),
        new ColumnSpec("SAMPLEGLACCOUNT", ColumnType.Text),
        new ColumnSpec("ISPROFITLOSSACCOUNT", ColumnType.FlagXorNull),
        new ColumnSpec("CREATEDBYUSER", ColumnType.Text),
        new ColumnSpec("PROFITLOSSACCOUNTTYPE", ColumnType.Text),
        new ColumnSpec("RECONCILIATIONACCOUNTTYPE", ColumnType.Text),
        new ColumnSpec("LINEITEMDISPLAYISENABLED", ColumnType.FlagXorNull),
        new ColumnSpec("ISOPENITEMMANAGED", ColumnType.FlagXorNull),
        new ColumnSpec("ALTERNATIVEGLACCOUNT", ColumnType.Text),
        new ColumnSpec("ACCTGDOCITMDISPLAYSEQUENCERULE", ColumnType.Text),
        new ColumnSpec("GLACCOUNTEXTERNAL", ColumnType.Text),
        new ColumnSpec("COUNTRYCHARTOFACCOUNTS", ColumnType.Text),
        new ColumnSpec("AUTHORIZATIONGROUP", ColumnType.Text),
        new ColumnSpec("TAXCATEGORY", ColumnType.Text),
        new ColumnSpec("ISAUTOMATICALLYPOSTED", ColumnType.FlagXorNull),
    };

    private static readonly ColumnSpec[] BicActiveCustomerSchema = new[]
    {
        new ColumnSpec("ParentRelationshipID", ColumnType.Integer, AllowEmpty: false),
        new ColumnSpec("ParentRelationshipName", ColumnType.Text),
        new ColumnSpec("RelationshipID", ColumnType.Integer, AllowEmpty: false),
        new ColumnSpec("RelationshipName", ColumnType.Text),
        new ColumnSpec("Subtype", ColumnType.Text),
        new ColumnSpec("ClientType", ColumnType.Text),
        new ColumnSpec("ClientID", ColumnType.Integer, AllowEmpty: false),
        new ColumnSpec("ClientName", ColumnType.Text),
        new ColumnSpec("Address1", ColumnType.Text),
        new ColumnSpec("Address2", ColumnType.Text),
        new ColumnSpec("City", ColumnType.Text),
        new ColumnSpec("State", ColumnType.Text),
        new ColumnSpec("Zip", ColumnType.Text),
        new ColumnSpec("Phone", ColumnType.Integer),
        new ColumnSpec("Fax", ColumnType.Integer),
        new ColumnSpec("NAICS", ColumnType.Integer),
        new ColumnSpec("Branch", ColumnType.Text),
        new ColumnSpec("ClientProfitCenter", ColumnType.Integer),
        new ColumnSpec("BankerID", ColumnType.Integer),
        new ColumnSpec("BankerName", ColumnType.Text),
        new ColumnSpec("CustomerSince", ColumnType.Text),
        new ColumnSpec("Access", ColumnType.Text),
        new ColumnSpec("Source", ColumnType.Text),
        new ColumnSpec("CustomerTaxIDNumber", ColumnType.Text),
        new ColumnSpec("ContactLastName", ColumnType.Text),
        new ColumnSpec("ContactMiddleName", ColumnType.Text),
        new ColumnSpec("ContactFirstName", ColumnType.Text),
        new ColumnSpec("ContactBirthDate", ColumnType.Text),
        new ColumnSpec("ContactBusPhone", ColumnType.Decimal),
        new ColumnSpec("ContactHomePhone", ColumnType.Decimal),
        new ColumnSpec("ContactMobilePhone", ColumnType.Decimal),
        new ColumnSpec("ContactEmailAddress", ColumnType.Text),
        new ColumnSpec("ContactDeceasedDate", ColumnType.Text),
        new ColumnSpec("ContactStatus", ColumnType.Text),
        new ColumnSpec("SecCodeID", ColumnType.Text),
        new ColumnSpec("ContactID", ColumnType.Text),
        new ColumnSpec("HHCIS", ColumnType.Integer),
        new ColumnSpec("HHGCIS", ColumnType.Decimal),
        new ColumnSpec("BankNumber", ColumnType.Integer),
        new ColumnSpec("ActiveStatus", ColumnType.Text),
    };

    // Stage 1: read just enough of the stream to validate the header.
    // Stream is rewound at the end so the caller can re-use it (or upload it).
    // This means a 1GB file with a bad header rejects in milliseconds, never reading
    // past the first line.
    public static async Task<ValidationResult> ValidateHeaderAsync(
        Stream stream,
        string fileName,
        string module,
        CancellationToken ct)
    {
        var errors = new List<string>();

        if (!ModuleSchemas.TryGetValue(module, out var schema))
        {
            errors.Add($"Module '{module}' is not registered for validation. Allowed: {string.Join(", ", ModuleSchemas.Keys)}");
            return new ValidationResult { IsValid = false, Errors = errors, Stage = "Stage1" };
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (module == "dataflow" && ext != ".csv")
            errors.Add($"DataFlow expects .csv files. Got '{ext}'.");
        if (module == "salesforce" && ext != ".txt")
            errors.Add($"Salesforce BIC expects .txt files. Got '{ext}'.");

        // Read just the header line from the stream, then rewind.
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var headerLine = await reader.ReadLineAsync(ct);
        stream.Position = 0;

        if (string.IsNullOrEmpty(headerLine))
        {
            errors.Add("File has no header line.");
            return new ValidationResult { IsValid = false, Errors = errors, Stage = "Stage1" };
        }

        var headerColumns = headerLine.TrimEnd('\r').Split(schema.Delimiter).Length;
        if (headerColumns != schema.Columns)
            errors.Add($"Header has {headerColumns} columns. Expected {schema.Columns} for {module} pipeline.");

        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            ColumnCount = headerColumns,
            Stage = "Stage1"
        };
    }

    // Stage 2: stream-validate every row column by column.
    // Stops at MaxErrorsReported errors so a totally broken file doesn't take forever.
    // Stream is rewound at the end so the caller can upload it.
    public static async Task<ValidationResult> ValidateRowsAsync(
        Stream stream,
        string module,
        CancellationToken ct)
    {
        ColumnSpec[] spec;
        char delim;
        string stage;
        switch (module)
        {
            case "dataflow":
                spec = GLAccountSchema; delim = ','; stage = "Stage2-GLAccount"; break;
            case "salesforce":
                spec = BicActiveCustomerSchema; delim = '\t'; stage = "Stage2-BicActiveCustomer"; break;
            default:
                return new ValidationResult { IsValid = true, Stage = "Stage2-Skipped" };
        }

        var errors = new List<string>();
        int rowsChecked = 0;

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        // Skip the header line.
        await reader.ReadLineAsync(ct);

        int lineNumber = 1;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && errors.Count < MaxErrorsReported)
        {
            lineNumber++;
            var row = line.TrimEnd('\r').TrimEnd();
            if (string.IsNullOrWhiteSpace(row)) continue;

            var cols = row.Split(delim);
            rowsChecked++;

            if (cols.Length < spec.Length)
            {
                errors.Add($"Row {lineNumber}: only {cols.Length} columns, expected {spec.Length}.");
                continue;
            }

            for (int c = 0; c < spec.Length && errors.Count < MaxErrorsReported; c++)
            {
                var raw = cols[c].Trim().Trim('"');
                var col = spec[c];
                var err = ValidateCell(raw, col);
                if (err != null)
                    errors.Add($"Row {lineNumber} Col {c + 1} [{col.Name}]: {err} (got '{Truncate(raw, 30)}')");
            }
        }

        stream.Position = 0;

        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            RowCount = rowsChecked,
            ColumnCount = spec.Length,
            Stage = stage
        };
    }

    private static string? ValidateCell(string value, ColumnSpec col)
    {
        var isEmpty = string.IsNullOrWhiteSpace(value) || value.Equals("NULL", StringComparison.OrdinalIgnoreCase);

        switch (col.Type)
        {
            case ColumnType.AnythingGoes: return null;
            case ColumnType.Text: return null;
            case ColumnType.TextRequired: return isEmpty ? "required, but value is empty" : null;
            case ColumnType.Integer:
                if (isEmpty) return col.AllowEmpty ? null : "required integer, but value is empty";
                return long.TryParse(value, out _) ? null : "not an integer";
            case ColumnType.Decimal:
                if (isEmpty) return col.AllowEmpty ? null : "required decimal, but value is empty";
                return decimal.TryParse(value, out _) ? null : "not a decimal";
            case ColumnType.Date:
                if (isEmpty) return col.AllowEmpty ? null : "required date, but value is empty";
                return DateTime.TryParse(value, out _) ? null : "not a date";
            case ColumnType.FlagXorNull:
                if (isEmpty) return null;
                return value == "X" ? null : "expected 'X' or NULL/empty";
            default: return null;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";
}
