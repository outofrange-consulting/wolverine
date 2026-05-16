using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

public class ApiVersionEndpointSelectorPolicyTests
{
    // ---------- Helpers ----------

    private static RouteEndpoint VersionedEndpoint(ApiVersion version, string displayName = "endpoint")
    {
        var declared = new[] { version };
        var model = new ApiVersionModel(
            declaredVersions: declared,
            supportedVersions: declared,
            deprecatedVersions: Array.Empty<ApiVersion>(),
            advertisedVersions: Array.Empty<ApiVersion>(),
            deprecatedAdvertisedVersions: Array.Empty<ApiVersion>());

        return new RouteEndpoint(
            requestDelegate: _ => Task.CompletedTask,
            routePattern: RoutePatternFactory.Parse("/orders"),
            order: 0,
            metadata: new EndpointMetadataCollection(new ApiVersionMetadata(model, model)),
            displayName: displayName);
    }

    private static RouteEndpoint NeutralEndpoint(string displayName = "neutral")
    {
        return new RouteEndpoint(
            requestDelegate: _ => Task.CompletedTask,
            routePattern: RoutePatternFactory.Parse("/orders"),
            order: 0,
            metadata: new EndpointMetadataCollection(ApiVersionMetadata.Neutral),
            displayName: displayName);
    }

    private static RouteEndpoint EndpointWithoutMetadata(string displayName = "no-meta")
    {
        return new RouteEndpoint(
            requestDelegate: _ => Task.CompletedTask,
            routePattern: RoutePatternFactory.Parse("/orders"),
            order: 0,
            metadata: EndpointMetadataCollection.Empty,
            displayName: displayName);
    }

    private static CandidateSet BuildCandidates(params Endpoint[] endpoints)
    {
        // CandidateSet's constructor takes parallel arrays of endpoints, route value sets, and scores.
        var values = new RouteValueDictionary[endpoints.Length];
        var scores = new int[endpoints.Length];
        for (var i = 0; i < endpoints.Length; i++)
        {
            values[i] = new RouteValueDictionary();
        }

        return new CandidateSet(endpoints, values, scores);
    }

    private static DefaultHttpContext BuildContext(
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyDictionary<string, string>? query = null)
    {
        var ctx = new DefaultHttpContext();
        if (headers is not null)
        {
            foreach (var kvp in headers)
                ctx.Request.Headers[kvp.Key] = kvp.Value;
        }

        if (query is not null)
        {
            // QueryString.Create's parameter type uses nullable values; project explicitly to silence
            // the CS8620 nullability mismatch in the test-only construction path.
            var pairs = query.Select(kvp => new KeyValuePair<string, string?>(kvp.Key, kvp.Value));
            ctx.Request.QueryString = QueryString.Create(pairs);
        }

        return ctx;
    }

    // ---------- AppliesToEndpoints ----------

    [Fact]
    public void applies_when_any_endpoint_has_explicit_version_metadata()
    {
        var policy = new ApiVersionEndpointSelectorPolicy(new WolverineApiVersioningOptions());
        var endpoints = new Endpoint[] { VersionedEndpoint(new ApiVersion(1, 0)), EndpointWithoutMetadata() };

        ((IEndpointSelectorPolicy)policy).AppliesToEndpoints(endpoints).ShouldBeTrue();
    }

    [Fact]
    public void does_not_apply_when_only_neutral_or_unversioned_endpoints_are_present()
    {
        var policy = new ApiVersionEndpointSelectorPolicy(new WolverineApiVersioningOptions());
        var endpoints = new Endpoint[] { NeutralEndpoint(), EndpointWithoutMetadata() };

        ((IEndpointSelectorPolicy)policy).AppliesToEndpoints(endpoints).ShouldBeFalse();
    }

    // ---------- ApplyAsync ----------

    [Fact]
    public async Task apply_is_noop_when_no_non_url_source_is_configured()
    {
        var options = new WolverineApiVersioningOptions();
        var policy = new ApiVersionEndpointSelectorPolicy(options);

        var ep1 = VersionedEndpoint(new ApiVersion(1, 0), "v1");
        var ep2 = VersionedEndpoint(new ApiVersion(2, 0), "v2");
        var candidates = BuildCandidates(ep1, ep2);
        var ctx = BuildContext();

        await ((IEndpointSelectorPolicy)policy).ApplyAsync(ctx, candidates);

        candidates.IsValidCandidate(0).ShouldBeTrue();
        candidates.IsValidCandidate(1).ShouldBeTrue();
    }

    [Fact]
    public async Task header_value_keeps_only_matching_clone()
    {
        var options = new WolverineApiVersioningOptions();
        options.ReadVersionFromHeader("X-Api-Version");
        var policy = new ApiVersionEndpointSelectorPolicy(options);

        var v1 = VersionedEndpoint(new ApiVersion(1, 0), "v1");
        var v2 = VersionedEndpoint(new ApiVersion(2, 0), "v2");
        var candidates = BuildCandidates(v1, v2);
        var ctx = BuildContext(headers: new Dictionary<string, string> { ["X-Api-Version"] = "2.0" });

        await ((IEndpointSelectorPolicy)policy).ApplyAsync(ctx, candidates);

        candidates.IsValidCandidate(0).ShouldBeFalse();
        candidates.IsValidCandidate(1).ShouldBeTrue();
    }

    [Fact]
    public async Task query_string_value_keeps_only_matching_clone()
    {
        var options = new WolverineApiVersioningOptions();
        options.ReadVersionFromQueryString("api-version");
        var policy = new ApiVersionEndpointSelectorPolicy(options);

        var v1 = VersionedEndpoint(new ApiVersion(1, 0), "v1");
        var v2 = VersionedEndpoint(new ApiVersion(2, 0), "v2");
        var candidates = BuildCandidates(v1, v2);
        var ctx = BuildContext(query: new Dictionary<string, string> { ["api-version"] = "1.0" });

        await ((IEndpointSelectorPolicy)policy).ApplyAsync(ctx, candidates);

        candidates.IsValidCandidate(0).ShouldBeTrue();
        candidates.IsValidCandidate(1).ShouldBeFalse();
    }

    [Fact]
    public async Task missing_header_with_default_falls_back_to_default_version()
    {
        var options = new WolverineApiVersioningOptions
        {
            AssumeDefaultVersionWhenUnspecified = true,
            DefaultVersion = new ApiVersion(1, 0)
        };
        options.ReadVersionFromHeader("X-Api-Version");
        var policy = new ApiVersionEndpointSelectorPolicy(options);

        var v1 = VersionedEndpoint(new ApiVersion(1, 0), "v1");
        var v2 = VersionedEndpoint(new ApiVersion(2, 0), "v2");
        var candidates = BuildCandidates(v1, v2);
        var ctx = BuildContext();

        await ((IEndpointSelectorPolicy)policy).ApplyAsync(ctx, candidates);

        candidates.IsValidCandidate(0).ShouldBeTrue();
        candidates.IsValidCandidate(1).ShouldBeFalse();
    }

    [Fact]
    public async Task missing_header_without_default_invalidates_versioned_candidates()
    {
        var options = new WolverineApiVersioningOptions();
        options.ReadVersionFromHeader("X-Api-Version");
        var policy = new ApiVersionEndpointSelectorPolicy(options);

        var v1 = VersionedEndpoint(new ApiVersion(1, 0), "v1");
        var v2 = VersionedEndpoint(new ApiVersion(2, 0), "v2");
        var neutral = NeutralEndpoint("neutral");
        var candidates = BuildCandidates(v1, v2, neutral);
        var ctx = BuildContext();

        await ((IEndpointSelectorPolicy)policy).ApplyAsync(ctx, candidates);

        candidates.IsValidCandidate(0).ShouldBeFalse();
        candidates.IsValidCandidate(1).ShouldBeFalse();
        // Neutral siblings stay valid so they can serve unversioned requests.
        candidates.IsValidCandidate(2).ShouldBeTrue();
    }

    [Fact]
    public async Task malformed_version_invalidates_versioned_candidates()
    {
        var options = new WolverineApiVersioningOptions();
        options.ReadVersionFromHeader("X-Api-Version");
        var policy = new ApiVersionEndpointSelectorPolicy(options);

        var v1 = VersionedEndpoint(new ApiVersion(1, 0), "v1");
        var candidates = BuildCandidates(v1);
        var ctx = BuildContext(headers: new Dictionary<string, string> { ["X-Api-Version"] = "not-a-version" });

        await ((IEndpointSelectorPolicy)policy).ApplyAsync(ctx, candidates);

        candidates.IsValidCandidate(0).ShouldBeFalse();
    }

    [Fact]
    public async Task ambiguous_sources_invalidates_versioned_candidates()
    {
        // Two configured sources supplying disagreeing values — fail closed.
        var options = new WolverineApiVersioningOptions();
        options.ReadVersionFromHeader("X-Api-Version");
        options.ReadVersionFromQueryString("api-version");
        var policy = new ApiVersionEndpointSelectorPolicy(options);

        var v1 = VersionedEndpoint(new ApiVersion(1, 0), "v1");
        var v2 = VersionedEndpoint(new ApiVersion(2, 0), "v2");
        var candidates = BuildCandidates(v1, v2);
        var ctx = BuildContext(
            headers: new Dictionary<string, string> { ["X-Api-Version"] = "1.0" },
            query: new Dictionary<string, string> { ["api-version"] = "2.0" });

        await ((IEndpointSelectorPolicy)policy).ApplyAsync(ctx, candidates);

        candidates.IsValidCandidate(0).ShouldBeFalse();
        candidates.IsValidCandidate(1).ShouldBeFalse();
    }

    [Fact]
    public async Task agreeing_sources_select_the_agreed_version()
    {
        var options = new WolverineApiVersioningOptions();
        options.ReadVersionFromHeader("X-Api-Version");
        options.ReadVersionFromQueryString("api-version");
        var policy = new ApiVersionEndpointSelectorPolicy(options);

        var v1 = VersionedEndpoint(new ApiVersion(1, 0), "v1");
        var v2 = VersionedEndpoint(new ApiVersion(2, 0), "v2");
        var candidates = BuildCandidates(v1, v2);
        var ctx = BuildContext(
            headers: new Dictionary<string, string> { ["X-Api-Version"] = "2.0" },
            query: new Dictionary<string, string> { ["api-version"] = "2.0" });

        await ((IEndpointSelectorPolicy)policy).ApplyAsync(ctx, candidates);

        candidates.IsValidCandidate(0).ShouldBeFalse();
        candidates.IsValidCandidate(1).ShouldBeTrue();
    }

    [Fact]
    public async Task neutral_endpoints_are_never_invalidated()
    {
        var options = new WolverineApiVersioningOptions();
        options.ReadVersionFromHeader("X-Api-Version");
        var policy = new ApiVersionEndpointSelectorPolicy(options);

        var v1 = VersionedEndpoint(new ApiVersion(1, 0), "v1");
        var neutral = NeutralEndpoint("neutral");
        var candidates = BuildCandidates(v1, neutral);
        var ctx = BuildContext(headers: new Dictionary<string, string> { ["X-Api-Version"] = "99.0" });

        await ((IEndpointSelectorPolicy)policy).ApplyAsync(ctx, candidates);

        candidates.IsValidCandidate(0).ShouldBeFalse();
        candidates.IsValidCandidate(1).ShouldBeTrue();
    }

    [Fact]
    public void read_version_from_header_rejects_blank_name()
    {
        var options = new WolverineApiVersioningOptions();
        Should.Throw<ArgumentException>(() => options.ReadVersionFromHeader(""));
        Should.Throw<ArgumentException>(() => options.ReadVersionFromHeader("   "));
    }

    [Fact]
    public void read_version_from_header_is_idempotent_case_insensitive()
    {
        var options = new WolverineApiVersioningOptions();
        options.ReadVersionFromHeader("X-Api-Version");
        options.ReadVersionFromHeader("x-api-version");

        options.VersionHeaderNames.Count.ShouldBe(1);
    }

    [Fact]
    public void has_non_url_version_source_reports_true_when_header_configured()
    {
        var options = new WolverineApiVersioningOptions();
        options.HasNonUrlVersionSource.ShouldBeFalse();

        options.ReadVersionFromHeader("X-Api-Version");
        options.HasNonUrlVersionSource.ShouldBeTrue();
    }
}
