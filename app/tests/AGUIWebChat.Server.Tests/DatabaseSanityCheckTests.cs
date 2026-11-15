using AGUIWebChat.Server.Data;
using AGUIWebChat.Server.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class DatabaseSanityCheckTests
{
    [Fact]
    public void FindPendingMigrationsReportsOnlyMigrationsThisBuildKnows()
    {
        string[] discovered = ["20260101000000_A", "20260202000000_B", "20260303000000_C"];
        string[] applied = ["20260101000000_A"];

        Assert.Equal(["20260202000000_B", "20260303000000_C"],
            DatabaseSanityCheck.FindPendingMigrations(discovered, applied));
    }

    [Fact]
    public void FindPendingMigrationsIgnoresAppliedRevisionsUnknownToThisBuild()
    {
        // A database migrated by a newer build is not "pending" for this one; it is reported by
        // EF as a rollback target, not as missing work.
        string[] discovered = ["20260101000000_A", "20260202000000_B"];
        string[] applied = ["20260101000000_A", "20260202000000_B", "20260404000000_FromNewerBuild"];

        Assert.Empty(DatabaseSanityCheck.FindPendingMigrations(discovered, applied));
    }

    [Fact]
    public async Task CheckReportsHealthyForInMemoryProvider()
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new ChatDbContext(options);

        DatabaseSanityReport report = await DatabaseSanityCheck.CheckAsync(
            context, TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseSanityStatus.Healthy, report.Status);
    }

    [Fact]
    public async Task CheckReportsHealthyForACreatedSchemaWithTheCatalogueTables()
    {
        await using var factory = await TestDbContextFactory.CreateAsync(TestContext.Current.CancellationToken);

        DatabaseSanityReport report = await DatabaseSanityCheck.CheckAsync(
            factory.DbContext, TestContext.Current.CancellationToken);

        // SQLite carries no __EFMigrationsHistory, so the migration probe must stay silent and
        // the report must not claim pending migrations.
        Assert.Equal(DatabaseSanityStatus.Healthy, report.Status);
        Assert.Empty(report.Remediation);
        Assert.DoesNotContain(report.Details,
            detail => detail.Contains("migration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckReportsSchemaMissingWhenTheCatalogueTableIsAbsent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<ChatDbContext>().UseSqlite(connection).Options;
        await using var emptyContext = new ChatDbContext(options);

        DatabaseSanityReport report = await DatabaseSanityCheck.CheckAsync(
            emptyContext, TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseSanityStatus.SchemaMissing, report.Status);
        Assert.Contains(report.Remediation,
            line => line.Contains("dotnet ef database update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckReportsUnreachableWhenTheDatabaseFileCannotBeOpened()
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseSqlite("Data Source=/nonexistent-directory-aguiwebchat/catalogue.db")
            .Options;
        await using var context = new ChatDbContext(options);

        DatabaseSanityReport report = await DatabaseSanityCheck.CheckAsync(
            context, TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseSanityStatus.Unreachable, report.Status);
        Assert.NotEmpty(report.Details);
    }

    [Fact]
    public void DiagnosticTextCarriesEverySectionAndNeverLeaksACredential()
    {
        var report = new DatabaseSanityReport(
            DatabaseSanityStatus.MigrationsPending,
            "The catalogue schema is 1 migration(s) behind this build.",
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Data Source=localhost,1433;User ID=sa;Password=***redacted***",
            ["Pending migrations: 20261005101601_SeedOllamaChatModel"],
            ["Apply the migrations: dotnet ef database update"],
            ["No enabled AI model row is present."]);

        string text = report.ToDiagnosticText("FAILED");

        Assert.Contains("DATABASE READINESS: FAILED", text);
        Assert.Contains("status      : MigrationsPending", text);
        Assert.Contains("summary     : ", text);
        Assert.Contains("provider    : ", text);
        Assert.Contains("target      : ", text);
        Assert.Contains("detail      : ", text);
        Assert.Contains("remediation : ", text);
        Assert.Contains("warning     : ", text);
        Assert.DoesNotContain("DemoPass123!", text);
    }

    [Fact]
    public async Task DatabaseNotReadyExceptionCarriesTheReportAndItsText()
    {
        var report = new DatabaseSanityReport(
            DatabaseSanityStatus.SchemaMissing,
            "The database is reachable but the AI catalogue tables are absent.",
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Data Source=localhost,1433",
            ["SqlException: Invalid object name 'AIModels'."],
            ["Apply the schema and seed data: dotnet ef database update --project src/Server/AGUIWebChat.Server.csproj"],
            []);

        var exception = new DatabaseNotReadyException(report);

        Assert.Same(report, exception.Report);
        Assert.Contains("DATABASE READINESS: FAILED", exception.Message);
        Assert.Contains("Invalid object name 'AIModels'.", exception.Message);
        Assert.Contains("dotnet ef database update", exception.Message);
    }

    [Fact]
    public async Task HealthCheckIsHealthyAndDescribesTheDatabaseWhenTheCatalogueIsPresent()
    {
        await using var factory = await TestDbContextFactory.CreateAsync(TestContext.Current.CancellationToken);

        HealthCheckResult result = await new DatabaseHealthCheck(factory.DbContext)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Healthy", result.Data["status"]);
    }

    [Fact]
    public async Task HealthCheckIsUnhealthyAndCarriesRemediationWhenTheCatalogueIsAbsent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<ChatDbContext>().UseSqlite(connection).Options;
        await using var emptyContext = new ChatDbContext(options);

        HealthCheckResult result = await new DatabaseHealthCheck(emptyContext)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        // Readiness must fail while liveness ("self") stays healthy, so orchestrators stop
        // routing traffic without restarting the process.
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("SchemaMissing", result.Data["status"]);
        Assert.Contains(result.Data.Values.OfType<string>(),
            value => value.Contains("dotnet ef database update", StringComparison.Ordinal));
        Assert.Contains("DATABASE READINESS: FAILED", result.Description);
    }

    [Fact]
    public void CatalogueUnavailableCoversMissingSchemaAndConnectionFailuresOnly()
    {
        Assert.True(DatabaseSanityCheck.IsCatalogueUnavailable(
            new InvalidOperationException("Invalid object name 'AIModels'.")));
        Assert.True(DatabaseSanityCheck.IsCatalogueUnavailable(
            new SqliteException("no such table: AIModels", 1)));
        Assert.False(DatabaseSanityCheck.IsCatalogueUnavailable(new InvalidOperationException("boom")));
    }
}
