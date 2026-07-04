using Asp.Versioning;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

// ---------- Test handler fixtures ----------
//
// These exercise "scenario 3" from the versioning docs: the API-version token is embedded directly
// in the route template (the standard ASP.NET Core `{version:apiVersion}` convention), rather than
// being injected via the config-level UrlSegmentPrefix. Wolverine substitutes the token with the
// concrete version at bootstrap, so the live route is a plain literal path and every OpenAPI
// generator sees a versioned path with no lingering {version} parameter.

internal class InlineConstraintOrdersV1Handler
{
    [WolverineGet("/api/v{version:apiVersion}/orders")]
    [ApiVersion("1.0")]
    public string Get() => "v1";
}

internal class InlineBareOrdersV2Handler
{
    [WolverineGet("/api/v{apiVersion}/orders")]
    [ApiVersion("2.0")]
    public string Get() => "v2";
}

internal class InlineMultiVersionOrdersHandler
{
    [WolverineGet("/api/v{version:apiVersion}/orders")]
    [ApiVersion("1.0")]
    [ApiVersion("2.0")]
    public string Get() => "multi";
}

internal class InlineNoVersionHandler
{
    [WolverineGet("/api/v{version:apiVersion}/orders")]
    public string Get() => "no-version";
}

internal class InlineOrdersV1DuplicateHandler
{
    [WolverineGet("/api/v{version:apiVersion}/orders")]
    [ApiVersion("1.0")]
    public string Get() => "v1-dup";
}

// ---------- Tests ----------

public class InlineRouteVersioningTests
{
    private static void Apply(ApiVersioningPolicy policy, params HttpChain[] chains)
        => policy.Apply(chains, new GenerationRules(), null!);

    [Fact]
    public void inline_constraint_token_is_substituted_in_place()
    {
        var opts = new WolverineApiVersioningOptions(); // default UrlSegmentPrefix = "v{version}"
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<InlineConstraintOrdersV1Handler>(x => x.Get());

        Apply(policy, chain);

        // The token is replaced by the concrete version and the URL-segment prefix is NOT applied
        // on top — the route is self-describing.
        chain.RoutePattern!.RawText.ShouldBe("/api/v1/orders");
    }

    [Fact]
    public void inline_bare_apiversion_token_is_substituted()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<InlineBareOrdersV2Handler>(x => x.Get());

        Apply(policy, chain);

        chain.RoutePattern!.RawText.ShouldBe("/api/v2/orders");
    }

    [Fact]
    public void substituted_route_has_no_leftover_version_parameter()
    {
        // The core OpenAPI-correctness guarantee: after substitution the route is a plain literal,
        // so Microsoft.AspNetCore.OpenApi / NSwag / Swashbuckle emit "/api/v1/orders" and never a
        // spurious {version} path parameter.
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<InlineConstraintOrdersV1Handler>(x => x.Get());

        chain.RoutePattern!.Parameters.ShouldContain(p => p.Name == "version");

        Apply(policy, chain);

        chain.RoutePattern!.Parameters.ShouldNotContain(p => p.Name == "version");
        chain.RoutePattern!.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void inline_token_substituted_when_url_segment_prefix_disabled()
    {
        // No-prefix mode (header/query-string versioning). The inline token must STILL be substituted
        // — otherwise the raw {version:apiVersion} token would reach ASP.NET Core routing and fault.
        var opts = new WolverineApiVersioningOptions { UrlSegmentPrefix = null };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<InlineConstraintOrdersV1Handler>(x => x.Get());

        Apply(policy, chain);

        chain.RoutePattern!.RawText.ShouldBe("/api/v1/orders");
    }

    [Fact]
    public void inline_substitution_honours_custom_version_formatter()
    {
        var opts = new WolverineApiVersioningOptions
        {
            UrlSegmentVersionFormatter = v =>
                v.MajorVersion.HasValue
                    ? $"{v.MajorVersion}.{v.MinorVersion ?? 0}"
                    : v.ToString()
        };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<InlineConstraintOrdersV1Handler>(x => x.Get());

        Apply(policy, chain);

        chain.RoutePattern!.RawText.ShouldBe("/api/v1.0/orders");
    }

    [Fact]
    public void inline_multi_version_expands_and_substitutes_each_clone()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);

        var chains = new List<HttpChain> { HttpChain.ChainFor<InlineMultiVersionOrdersHandler>(x => x.Get()) };
        MultiVersionExpansion.ExpandInPlace(chains);
        chains.Count.ShouldBe(2);

        Apply(policy, chains.ToArray());

        chains.Select(c => c.RoutePattern!.RawText).OrderBy(x => x)
            .ShouldBe(new[] { "/api/v1/orders", "/api/v2/orders" });
    }

    [Fact]
    public void inline_multi_version_clones_advertise_the_full_supported_set()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);

        var chains = new List<HttpChain> { HttpChain.ChainFor<InlineMultiVersionOrdersHandler>(x => x.Get()) };
        MultiVersionExpansion.ExpandInPlace(chains);
        Apply(policy, chains.ToArray());

        foreach (var chain in chains)
        {
            var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
            var meta = endpoint.Metadata.GetMetadata<ApiVersionMetadata>();
            meta.ShouldNotBeNull();

            // Siblings group on the shared logical route (which still carries the un-substituted
            // token), so every clone advertises the union 1.0 + 2.0 as supported.
            var supported = meta!.Map(ApiVersionMapping.Explicit).SupportedApiVersions;
            supported.ShouldContain(new ApiVersion(1, 0));
            supported.ShouldContain(new ApiVersion(2, 0));
        }
    }

    [Fact]
    public void inline_chain_receives_group_name_metadata()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<InlineConstraintOrdersV1Handler>(x => x.Get());

        Apply(policy, chain);
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        endpoint.Metadata.GetMetadata<IEndpointGroupNameMetadata>()!.EndpointGroupName.ShouldBe("v1");
    }

    [Fact]
    public void inline_token_without_resolved_version_throws()
    {
        var opts = new WolverineApiVersioningOptions { UnversionedPolicy = UnversionedPolicy.PassThrough };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<InlineNoVersionHandler>(x => x.Get());

        var ex = Should.Throw<InvalidOperationException>(() => Apply(policy, chain));
        ex.Message.ShouldContain("inline API-version token");
        ex.Message.ShouldContain("/api/v{version:apiVersion}/orders");
    }

    [Fact]
    public void prefix_without_version_token_does_not_throw_for_inline_chains()
    {
        // A prefix lacking {version} normally throws, but an inline chain never consumes the prefix,
        // so validation must not fire when the only versioned chains are inline.
        var opts = new WolverineApiVersioningOptions { UrlSegmentPrefix = "api" };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<InlineConstraintOrdersV1Handler>(x => x.Get());

        Should.NotThrow(() => Apply(policy, chain));
        chain.RoutePattern!.RawText.ShouldBe("/api/v1/orders");
    }

    [Fact]
    public void inline_substitution_is_idempotent()
    {
        var opts = new WolverineApiVersioningOptions(); // default prefix present
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<InlineConstraintOrdersV1Handler>(x => x.Get());

        Apply(policy, chain);
        var afterFirst = chain.RoutePattern!.RawText;

        Apply(policy, chain);

        // Must NOT double-prefix (e.g. "/v1/api/v1/orders") on a second pass.
        chain.RoutePattern!.RawText.ShouldBe(afterFirst);
        chain.RoutePattern!.RawText.ShouldBe("/api/v1/orders");
    }

    [Fact]
    public void wolverine_web_api_sample_endpoint_expands_and_substitutes()
    {
        // Guards the real WolverineWebApi sample endpoint (bare {apiVersion} token + multi-version)
        // against the exact policy pipeline the integration host runs, so a bootstrap regression is
        // caught here without needing to boot the database-backed host.
        var opts = new WolverineApiVersioningOptions(); // matches Program.cs defaults (prefix present, PassThrough)
        var policy = new ApiVersioningPolicy(opts);

        var chains = new List<HttpChain>
        {
            HttpChain.ChainFor(
                typeof(WolverineWebApi.ApiVersioning.InlineVersionedOrdersEndpoint),
                nameof(WolverineWebApi.ApiVersioning.InlineVersionedOrdersEndpoint.Get))
        };
        MultiVersionExpansion.ExpandInPlace(chains);
        Apply(policy, chains.ToArray());

        chains.Select(c => c.RoutePattern!.RawText).OrderBy(x => x)
            .ShouldBe(new[] { "/api/inline/v1/orders", "/api/inline/v2/orders" });
    }

    [Fact]
    public void duplicate_inline_route_and_version_throws()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain1 = HttpChain.ChainFor<InlineConstraintOrdersV1Handler>(x => x.Get());
        var chain2 = HttpChain.ChainFor<InlineOrdersV1DuplicateHandler>(x => x.Get());

        var ex = Should.Throw<InvalidOperationException>(() => Apply(policy, chain1, chain2));
        ex.Message.ShouldContain("1.0");
    }
}
