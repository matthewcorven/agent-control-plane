using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace AGUIWebChat.Server.Data;

/// <summary>Outcome of a sanity check against the AI model catalogue database.</summary>
public enum DatabaseSanityStatus
{
    /// <summary>The database is reachable, has the catalogue schema, and is migrated.</summary>
    Healthy,

    /// <summary>The database could not be contacted at all.</summary>
    Unreachable,

    /// <summary>The database answered but the catalogue tables are absent.</summary>
    SchemaMissing,

    /// <summary>The catalogue schema exists but is behind the migrations in this build.</summary>
    MigrationsPending
}

/// <summary>
/// A structured, human- and agent-readable readiness report. <see cref="ToDiagnosticText"/>
/// renders the same block that is written to the log and, when the check is fatal, carried by
/// the startup exception — so logs, crash output and bug reports all show identical remediation
/// steps. Credentials are never included; see <see cref="DatabaseSanityCheck.CheckAsync"/>.
/// </summary>
public sealed record DatabaseSanityReport(
    DatabaseSanityStatus Status,
    string Summary,
    string Provider,
    string Target,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> Remediation,
    IReadOnlyList<string> Warnings)
{
    public bool IsHealthy => Status == DatabaseSanityStatus.Healthy;

    /// <summary>Renders a fixed-width ASCII block so logs stay greppable and copy-pasteable.</summary>
    public string ToDiagnosticText(string heading)
    {
        const string Rule = "================================================================================";

        var builder = new StringBuilder();

        builder.AppendLine(Rule);
        builder.AppendLine($"DATABASE READINESS: {heading}");
        builder.AppendLine(Rule);
        builder.AppendLine($"status      : {Status}");
        builder.AppendLine($"summary     : {Summary}");
        builder.AppendLine($"provider    : {Provider}");
        builder.AppendLine($"target      : {Target}");

        AppendList(builder, "detail", Details);
        AppendList(builder, "remediation", Remediation);
        AppendList(builder, "warning", Warnings);

        builder.Append(Rule);

        return builder.ToString();

        static void AppendList(StringBuilder builder, string label, IReadOnlyList<string> values)
        {
            for (int index = 0; index < values.Count; index++)
            {
                string prefix = index == 0 ? $"{label,-12}: " : new string(' ', 14);
                builder.AppendLine(prefix + values[index]);

                // Continuation lines of a multi-line entry stay aligned under the first line.
                foreach (string line in values[index].Split('\n').Skip(1))
                {
                    builder.AppendLine(new string(' ', 14) + line);
                }
            }
        }
    }
}

/// <summary>
/// Raised at startup when the catalogue database fails its sanity check. The message is the
/// full diagnostic block, so an unhandled-exception dump is self-documenting.
/// </summary>
public sealed class DatabaseNotReadyException : InvalidOperationException
{
    public DatabaseNotReadyException(DatabaseSanityReport report)
        : base(report.ToDiagnosticText("FAILED"))
    {
        Report = report;
    }

    public DatabaseSanityReport Report { get; }
}

/// <summary>
/// Verifies the catalogue database before the server starts serving: can it be reached, does it
/// carry the catalogue schema, and is it migrated to this build's latest revision. The AppHost
/// provisions an empty SQL Server and never applies migrations, so without this check the first
/// query throws a bare <c>SqlException: Invalid object name 'AIModels'</c> with no hint that a
/// one-time <c>dotnet ef database update</c> is the fix.
/// </summary>
public static class DatabaseSanityCheck
{
    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string InMemoryProvider = "Microsoft.EntityFrameworkCore.InMemory";

    private const string MigrationCommand =
        "dotnet ef database update --project src/Server/AGUIWebChat.Server.csproj";

    private const string RestartCommand = "aspire resource server start";

    private const string DocsPointer =
        "docs/README.md -> \"Database migrations are a separate step\"";

    public static async Task<DatabaseSanityReport> CheckAsync(
        ChatDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        string provider = dbContext.Database.ProviderName ?? "unknown";
        string target = DescribeTarget(dbContext);

        // The in-memory provider used by E2E runs has no schema to verify: EnsureCreated
        // builds it and seeds it later in startup.
        if (provider == InMemoryProvider)
        {
            return new DatabaseSanityReport(
                DatabaseSanityStatus.Healthy,
                "In-memory provider in use; schema and seed data come from EnsureCreated.",
                provider,
                target,
                [],
                [],
                []);
        }

        bool reachable;
        string? connectionError = null;

        try
        {
            reachable = await dbContext.Database.CanConnectAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            reachable = false;
            connectionError = Describe(exception);
        }

        if (!reachable)
        {
            return new DatabaseSanityReport(
                DatabaseSanityStatus.Unreachable,
                "The catalogue database could not be contacted.",
                provider,
                target,
                connectionError is null
                    ? ["CanConnectAsync returned false without an error; the host answered but the database is missing."]
                    : [connectionError],
                [
                    $"Confirm SQL Server is running and healthy (Aspire 'sql' resource, or 'docker compose up -d').",
                    "Confirm ConnectionStrings:ChatDatabase (or ConnectionStrings__ChatDatabase) points at that instance.",
                    $"Then restart the server: {RestartCommand}"
                ],
                []);
        }

        try
        {
            // Any query proves the table exists. A missing table throws here rather than at the
            // first real request, which is the whole point of this check.
            await dbContext.AIModels.AsNoTracking().AnyAsync(cancellationToken);
        }
        catch (Exception exception) when (IsMissingSchema(exception))
        {
            return new DatabaseSanityReport(
                DatabaseSanityStatus.SchemaMissing,
                "The database is reachable but the AI catalogue tables are absent.",
                provider,
                target,
                [Describe(exception)],
                [
                    $"Apply the schema and seed data: {MigrationCommand}",
                    $"Then restart the server: {RestartCommand}",
                    DocsPointer
                ],
                []);
        }
        catch (Exception exception)
        {
            return new DatabaseSanityReport(
                DatabaseSanityStatus.Unreachable,
                "The catalogue schema probe failed.",
                provider,
                target,
                [Describe(exception)],
                [
                    "Confirm the connection string is correct and the account has read access to the catalogue tables.",
                    $"Then restart the server: {RestartCommand}"
                ],
                []);
        }

        // Only SQL Server uses the migration history: E2E SQLite databases are built by
        // EnsureCreated, which writes no __EFMigrationsHistory rows.
        if (provider == SqlServerProvider)
        {
            IReadOnlyList<string> pending = await FindPendingMigrationsAsync(dbContext, cancellationToken);

            if (pending.Count > 0)
            {
                return new DatabaseSanityReport(
                    DatabaseSanityStatus.MigrationsPending,
                    $"The catalogue schema is {pending.Count} migration(s) behind this build.",
                    provider,
                    target,
                    [$"Pending migrations: {string.Join(", ", pending)}"],
                    [
                        $"Apply the migrations: {MigrationCommand}",
                        $"Then restart the server: {RestartCommand}",
                        DocsPointer
                    ],
                    []);
            }
        }

        var warnings = new List<string>();

        bool hasEnabledModel = await dbContext.AIModels
            .AsNoTracking()
            .AnyAsync(model => model.IsEnabled && model.Provider.IsEnabled, cancellationToken);

        if (!hasEnabledModel)
        {
            warnings.Add(
                "No enabled AI model row is present; the server falls back to OLLAMA_MODEL (or the configured Foundry deployment).");
        }

        return new DatabaseSanityReport(
            DatabaseSanityStatus.Healthy,
            "The catalogue database is reachable, migrated, and holds the catalogue schema.",
            provider,
            target,
            [],
            [],
            warnings);
    }

    /// <summary>
    /// Migrations known to this build but not recorded in the database. Applied migrations that
    /// the assembly no longer knows about are ignored: they describe a rollback, not a pending
    /// upgrade, and are reported by EF separately.
    /// </summary>
    public static IReadOnlyList<string> FindPendingMigrations(
        IEnumerable<string> discovered,
        IEnumerable<string> applied)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(applied);

        var appliedSet = new HashSet<string>(applied, StringComparer.Ordinal);

        return discovered.Where(migration => !appliedSet.Contains(migration)).ToList();
    }

    private static async Task<IReadOnlyList<string>> FindPendingMigrationsAsync(
        ChatDbContext dbContext,
        CancellationToken cancellationToken)
    {
        // Discovered migrations are read from the assembly (no I/O), so only the applied set is
        // fetched asynchronously.
        IEnumerable<string> discovered = dbContext.Database.GetMigrations();
        IEnumerable<string> applied = await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken);

        return FindPendingMigrations(discovered, applied);
    }

    /// <summary>
    /// Provider-agnostic shape check: SqlClient reports "Invalid object name 'AIModels'" (error
    /// 208) and SQLite reports "no such table: AIModels". Matching the message keeps this file
    /// free of provider-specific references so it compiles for every host, including tests.
    /// </summary>
    public static bool IsMissingSchema(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the exception means the catalogue cannot be read: the schema is absent or the
    /// connection failed. Used to degrade gracefully instead of crashing mid-request.
    /// </summary>
    public static bool IsCatalogueUnavailable(Exception exception) =>
        IsMissingSchema(exception) || exception is DbException;

    private static string Describe(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException or InvalidOperationException)
            {
                return $"{current.GetType().Name}: {current.Message}";
            }
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }

    /// <summary>
    /// The connection string with every secret-bearing key redacted, so the diagnostic block is
    /// safe to paste into a log aggregator, an issue, or an agent prompt.
    /// </summary>
    private static string DescribeTarget(ChatDbContext dbContext)
    {
        if (!dbContext.Database.IsRelational())
        {
            return "(non-relational database)";
        }

        string? connectionString = dbContext.Database.GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return "(no connection string configured)";
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

            foreach (string key in builder.Keys.Cast<string>().ToArray())
            {
                if (IsSecretKey(key))
                {
                    builder[key] = "***redacted***";
                }
            }

            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return "(unparsable connection string)";
        }

        static bool IsSecretKey(string key) =>
            key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("pwd", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("access token", StringComparison.OrdinalIgnoreCase);
    }
}
