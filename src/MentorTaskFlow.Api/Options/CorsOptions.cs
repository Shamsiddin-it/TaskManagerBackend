namespace MentorTaskFlow.Api.Options;

/// <summary>
/// CORS configuration. <c>SEC-006</c>: credentials are allowed only for explicitly listed origins;
/// a wildcard origin is forbidden.
/// </summary>
/// <remarks>
/// The production origin is <c>https://app.&lt;domain&gt;</c> (<c>DEPLOY-012</c>). The domain is not
/// yet assigned, so the list is empty by default and is supplied per environment via
/// <c>Cors__AllowedOrigins__0</c>. An empty list means no cross-origin browser request is accepted —
/// deliberately fail-closed.
/// </remarks>
public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    public const string PolicyName = "MentorTaskFlowCors";

    public string[] AllowedOrigins { get; init; } = [];
}
