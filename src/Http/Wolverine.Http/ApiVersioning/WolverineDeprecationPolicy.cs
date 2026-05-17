using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Per-endpoint deprecation policy for RFC 9745 <c>Deprecation</c> and RFC 8288 <c>Link</c>
/// response-header emission. Wolverine ships a first-party policy record rather than reusing
/// <c>Asp.Versioning.DeprecationPolicy</c> because that type only exists in
/// <c>Asp.Versioning.Abstractions</c> v10, while Wolverine.Http now depends on
/// <c>Asp.Versioning.Http</c> 8.1.1, which is pinned against Abstractions v8 binaries — v8 has
/// <see cref="SunsetPolicy"/> but no deprecation companion. Keeping a Wolverine-owned shape also
/// decouples the public chain API from the package's churn between major versions.
/// </summary>
/// <param name="Date">The optional date after which the API version is considered deprecated; emitted as the RFC 9745 <c>Deprecation</c> header value.</param>
/// <param name="Links">Optional related links emitted as RFC 8288 <c>Link</c> header entries with <c>rel="deprecation"</c>.</param>
public sealed record WolverineDeprecationPolicy(DateTimeOffset? Date, IReadOnlyList<LinkHeaderValue> Links)
{
    /// <summary>Creates an empty policy with no scheduled date and no links — the chain is deprecated as of now.</summary>
    public WolverineDeprecationPolicy() : this(null, Array.Empty<LinkHeaderValue>()) { }

    /// <summary>Creates a policy with only a scheduled date.</summary>
    public WolverineDeprecationPolicy(DateTimeOffset date) : this(date, Array.Empty<LinkHeaderValue>()) { }

    /// <summary>Creates a policy with only a single related link.</summary>
    public WolverineDeprecationPolicy(LinkHeaderValue link) : this(null, new[] { link }) { }

    /// <summary>Creates a policy with both a scheduled date and a single related link.</summary>
    public WolverineDeprecationPolicy(DateTimeOffset date, LinkHeaderValue link) : this(date, new[] { link }) { }
}
