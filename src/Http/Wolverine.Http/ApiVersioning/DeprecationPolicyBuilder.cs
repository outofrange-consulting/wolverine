using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Fluent builder for configuring a deprecation policy on a specific API version.
/// Obtain an instance from <see cref="WolverineApiVersioningOptions.Deprecate(ApiVersion)"/>.
/// </summary>
public interface IWolverineDeprecationPolicyBuilder
{
    /// <summary>Set the deprecation date for this version.</summary>
    /// <param name="date">The date on which the version is deprecated.</param>
    /// <returns>The builder for chaining.</returns>
    IWolverineDeprecationPolicyBuilder On(DateTimeOffset date);

    /// <summary>
    /// Add an RFC 8288 Link header reference pointing to information about this deprecation.
    /// </summary>
    /// <param name="uri">The link target URI.</param>
    /// <param name="title">Optional human-readable title for the link.</param>
    /// <param name="type">Optional media type hint for the linked resource.</param>
    /// <returns>The builder for chaining.</returns>
    IWolverineDeprecationPolicyBuilder WithLink(Uri uri, string? title = null, string? type = null);
}

internal sealed class DeprecationPolicyBuilder : IWolverineDeprecationPolicyBuilder
{
    private readonly WolverineApiVersioningOptions _options;
    private readonly ApiVersion _version;
    private DateTimeOffset? _date;
    private readonly List<LinkHeaderValue> _links = new();

    internal DeprecationPolicyBuilder(WolverineApiVersioningOptions options, ApiVersion version)
    {
        _options = options;
        _version = version;
    }

    /// <inheritdoc/>
    public IWolverineDeprecationPolicyBuilder On(DateTimeOffset date)
    {
        _date = date;
        CommitPolicy();
        return this;
    }

    /// <inheritdoc/>
    public IWolverineDeprecationPolicyBuilder WithLink(Uri uri, string? title = null, string? type = null)
    {
        var link = new LinkHeaderValue(uri, "deprecation");
        if (title != null) link.Title = title;
        if (type != null) link.Type = type;
        _links.Add(link);
        CommitPolicy();
        return this;
    }

    private void CommitPolicy()
    {
        // Wolverine's first-party policy record carries everything we need (date + link list).
        // The full list is captured up front rather than appended after construction, which keeps
        // the type immutable and easier to reason about than the package's mutable variant.
        var links = _links.Count == 0 ? Array.Empty<LinkHeaderValue>() : _links.ToArray();
        _options.DeprecationPolicies[_version] = new WolverineDeprecationPolicy(_date, links);
    }
}
