namespace AGUIWebChat.Server.Inference;

/// <summary>
/// Microsoft Foundry connection settings. The <c>Foundry</c> configuration section is the
/// baseline; the <c>FOUNDRY_*</c> environment variables win so an Aspire AppHost can inject
/// them without editing appsettings.
/// </summary>
public sealed record FoundryOptions(string? Endpoint, string? Model, string? ReasoningModel)
{
    public const string SectionName = "Foundry";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);

    public static FoundryOptions Resolve(IConfiguration configuration)
    {
        return new FoundryOptions(
            FirstNonEmpty(
                configuration["FOUNDRY_URI"],
                configuration[$"{SectionName}:Endpoint"]),
            FirstNonEmpty(
                configuration["FOUNDRY_MODEL"],
                configuration[$"{SectionName}:Model"]),
            FirstNonEmpty(
                configuration["FOUNDRY_REASONING_MODEL"],
                configuration[$"{SectionName}:ReasoningModel"]));
    }

    /// <summary>
    /// Returns the endpoint to use, preferring the catalogue provider's own endpoint so a single
    /// deployment can point at several Foundry resources.
    /// </summary>
    public Uri ResolveEndpoint(string? catalogueEndpoint)
    {
        string? candidate = FirstNonEmpty(catalogueEndpoint, Endpoint);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new FoundryNotConfiguredException(
                "No Foundry endpoint is configured. Set Foundry:Endpoint or FOUNDRY_URI, " +
                "or set AIProvider.Endpoint on the Foundry provider row.");
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new FoundryNotConfiguredException(
                $"The Foundry endpoint '{candidate}' is not an absolute https URI.");
        }

        return uri;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
