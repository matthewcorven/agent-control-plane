using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AGUIWebChat.Server.Data;

/// <summary>
/// Exposes the catalogue database sanity check on /health so readiness reflects the database
/// state for as long as the server runs, not only at startup. The description carries the same
/// remediation text the startup diagnostic prints, so a failing /health response is actionable
/// on its own.
/// </summary>
public sealed class DatabaseHealthCheck(ChatDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        DatabaseSanityReport report = await DatabaseSanityCheck.CheckAsync(dbContext, cancellationToken);

        string description = report.IsHealthy
            ? report.Warnings.Count == 0
                ? report.Summary
                : $"{report.Summary} Warning: {string.Join(" ", report.Warnings)}"
            : report.ToDiagnosticText("FAILED");

        var data = new Dictionary<string, object>
        {
            ["status"] = report.Status.ToString(),
            ["provider"] = report.Provider,
            ["target"] = report.Target
        };

        for (int index = 0; index < report.Remediation.Count; index++)
        {
            data[$"remediation_{index}"] = report.Remediation[index];
        }

        return report.Status switch
        {
            DatabaseSanityStatus.Healthy => HealthCheckResult.Healthy(description, data),

            // Unreachable or un-migrated are both "not ready" rather than "broken": traffic must
            // not be routed here, but the process itself is alive.
            _ => HealthCheckResult.Unhealthy(description, data: data)
        };
    }
}
