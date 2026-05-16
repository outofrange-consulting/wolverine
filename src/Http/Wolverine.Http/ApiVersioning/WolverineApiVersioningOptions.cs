using System.Globalization;
using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Configuration options for Wolverine's native API versioning support. Pass an instance to
/// <see cref="WolverineHttpOptions.UseApiVersioning"/> to configure URL-segment behaviour,
/// unversioned-endpoint policy, and per-version sunset / deprecation policies.
/// </summary>
public sealed class WolverineApiVersioningOptions
{
    /// <summary>
    /// URL-segment template injected ahead of versioned routes. The literal <c>{version}</c> is
    /// replaced with the formatted version string produced by <see cref="UrlSegmentVersionFormatter"/>.
    /// Set to <see langword="null"/> to disable URL-segment versioning.
    /// </summary>
    /// <remarks>
    /// Must contain the literal '{version}' token when non-null. Setting a prefix without
    /// the token causes <see cref="ApiVersioningPolicy"/> to throw at startup. Set to null
    /// to disable URL-segment versioning entirely.
    /// </remarks>
    public string? UrlSegmentPrefix { get; set; } = "v{version}";

    /// <summary>
    /// Formatter producing the version string substituted into <see cref="UrlSegmentPrefix"/>.
    /// Defaults to major-only (e.g. <c>"1"</c> for <c>ApiVersion(1, 0)</c> rather than <c>"1.0"</c>).
    /// </summary>
    /// <remarks>
    /// Date-based versions (where <see cref="ApiVersion.MajorVersion"/> is null) fall back to
    /// <see cref="ApiVersion.ToString()"/> which may include hyphens. Override this formatter
    /// if your URL scheme requires a different shape for date-based versions.
    /// </remarks>
    public Func<ApiVersion, string> UrlSegmentVersionFormatter { get; set; }
        = static v => v.MajorVersion?.ToString(CultureInfo.InvariantCulture) ?? v.ToString();

    /// <summary>
    /// Behaviour for endpoints that do not declare an <c>[ApiVersion]</c> attribute.
    /// Defaults to <see cref="UnversionedPolicy.PassThrough"/>.
    /// </summary>
    public UnversionedPolicy UnversionedPolicy { get; set; } = UnversionedPolicy.PassThrough;

    /// <summary>
    /// Used when <see cref="UnversionedPolicy"/> is <see cref="UnversionedPolicy.AssignDefault"/>
    /// or <see cref="AssumeDefaultVersionWhenUnspecified"/> is <see langword="true"/>. Required in
    /// either of those modes; otherwise ignored.
    /// </summary>
    public ApiVersion? DefaultVersion { get; set; }

    /// <summary>
    /// HTTP header names that <see cref="ApiVersionEndpointSelectorPolicy"/> reads at request time
    /// to resolve the requested version. Adding a name to this list is required to enable
    /// header-based version selection — it does not replace URL-segment versioning, the two can
    /// coexist on the same chain (URL routing eliminates URL-mismatched candidates first; this
    /// policy then disambiguates clones that share the same URL via the request header).
    /// </summary>
    public IList<string> VersionHeaderNames { get; } = new List<string>();

    /// <summary>
    /// Query-string parameter names that <see cref="ApiVersionEndpointSelectorPolicy"/> reads at
    /// request time, parallel to <see cref="VersionHeaderNames"/>.
    /// </summary>
    public IList<string> VersionQueryStringNames { get; } = new List<string>();

    /// <summary>
    /// When <see langword="true"/>, requests that arrive without a version on any configured
    /// source resolve to <see cref="DefaultVersion"/>. Defaults to <see langword="false"/>, in
    /// which case unversioned requests against a versioned route resolve to 404 unless an
    /// unversioned sibling exists at the same route.
    /// </summary>
    public bool AssumeDefaultVersionWhenUnspecified { get; set; }

    /// <summary>
    /// Convenience helper that adds <paramref name="headerName"/> to <see cref="VersionHeaderNames"/>
    /// if not already present. Returns this options instance for fluent chaining.
    /// </summary>
    /// <param name="headerName">A non-empty HTTP header name (e.g. <c>"X-Api-Version"</c>).</param>
    public WolverineApiVersioningOptions ReadVersionFromHeader(string headerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        if (!VersionHeaderNames.Contains(headerName, StringComparer.OrdinalIgnoreCase))
            VersionHeaderNames.Add(headerName);
        return this;
    }

    /// <summary>
    /// Convenience helper that adds <paramref name="parameterName"/> to
    /// <see cref="VersionQueryStringNames"/> if not already present. Returns this options instance.
    /// </summary>
    /// <param name="parameterName">A non-empty query-string parameter name (e.g. <c>"api-version"</c>).</param>
    public WolverineApiVersioningOptions ReadVersionFromQueryString(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        if (!VersionQueryStringNames.Contains(parameterName, StringComparer.OrdinalIgnoreCase))
            VersionQueryStringNames.Add(parameterName);
        return this;
    }

    /// <summary>True when at least one non-URL version source (header or query string) is configured.</summary>
    internal bool HasNonUrlVersionSource =>
        VersionHeaderNames.Count > 0 || VersionQueryStringNames.Count > 0;

    /// <summary>
    /// Emit the <c>api-supported-versions</c> response header on every versioned endpoint.
    /// </summary>
    public bool EmitApiSupportedVersionsHeader { get; set; } = true;

    /// <summary>
    /// Emit RFC 9745 <c>Deprecation</c> and RFC 8594 <c>Sunset</c>/<c>Link</c> headers on endpoints
    /// that have a configured policy.
    /// </summary>
    public bool EmitDeprecationHeaders { get; set; } = true;

    /// <summary>OpenAPI integration options.</summary>
    public WolverineApiVersioningOpenApiOptions OpenApi { get; } = new();

    /// <summary>
    /// Per-version sunset policies. Populated via <see cref="Sunset(ApiVersion)"/> or
    /// <see cref="Sunset(string)"/>.
    /// </summary>
    internal Dictionary<ApiVersion, SunsetPolicy> SunsetPolicies { get; } = new();

    /// <summary>
    /// Per-version deprecation policies. Populated via <see cref="Deprecate(ApiVersion)"/> or
    /// <see cref="Deprecate(string)"/>.
    /// </summary>
    internal Dictionary<ApiVersion, DeprecationPolicy> DeprecationPolicies { get; } = new();

    /// <summary>Configure a sunset policy for the given version.</summary>
    /// <param name="version">The API version to configure a sunset policy for.</param>
    /// <returns>A builder that can be used to set dates and link references.</returns>
    public IWolverineSunsetPolicyBuilder Sunset(ApiVersion version) => new SunsetPolicyBuilder(this, version);

    /// <summary>Convenience overload that parses the version string (e.g. <c>"1.0"</c>).</summary>
    /// <param name="version">A version string such as <c>"1.0"</c> or <c>"2"</c>.</param>
    /// <returns>A builder that can be used to set dates and link references.</returns>
    public IWolverineSunsetPolicyBuilder Sunset(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return Sunset(ApiVersionParser.Default.Parse(version));
    }

    /// <summary>Configure a deprecation policy for the given version.</summary>
    /// <param name="version">The API version to configure a deprecation policy for.</param>
    /// <returns>A builder that can be used to set dates and link references.</returns>
    public IWolverineDeprecationPolicyBuilder Deprecate(ApiVersion version) => new DeprecationPolicyBuilder(this, version);

    /// <summary>Convenience overload that parses the version string (e.g. <c>"1.0"</c>).</summary>
    /// <param name="version">A version string such as <c>"1.0"</c> or <c>"2"</c>.</param>
    /// <returns>A builder that can be used to set dates and link references.</returns>
    public IWolverineDeprecationPolicyBuilder Deprecate(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return Deprecate(ApiVersionParser.Default.Parse(version));
    }
}
