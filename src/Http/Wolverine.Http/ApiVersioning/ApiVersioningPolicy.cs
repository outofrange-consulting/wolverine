using System.Text.RegularExpressions;
using Asp.Versioning;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// An <see cref="IHttpPolicy"/> that applies API versioning semantics to every
/// <see cref="HttpChain"/> during bootstrapping. Steps run in order:
/// <list type="bullet">
///   <item><description>A — Resolve <c>[ApiVersion]</c> attributes on handler methods.</description></item>
///   <item><description>B — Apply <see cref="UnversionedPolicy"/> to chains that remain unversioned.</description></item>
///   <item><description>C — Attach sunset / deprecation policies from <see cref="WolverineApiVersioningOptions"/>.</description></item>
///   <item><description>D — Reject duplicate (verb, route, version) triples.</description></item>
///   <item><description>E — Place the version in the route: substitute an inline <c>{version:apiVersion}</c> / <c>{apiVersion}</c> token in place, or otherwise prepend the URL-segment version prefix.</description></item>
///   <item><description>F — Attach group-name and <c>Asp.Versioning.ApiVersionMetadata</c> to the endpoint.</description></item>
///   <item><description>G — Attach the per-chain header state metadata read by the writer at request time.</description></item>
/// </list>
/// </summary>
internal sealed class ApiVersioningPolicy : IHttpPolicy
{
    /// <summary>
    /// Matches an inline API-version route token — either the ASP.NET Core convention
    /// <c>{version:apiVersion}</c> (any parameter name carrying the <c>apiVersion</c> route
    /// constraint) or the bare <c>{apiVersion}</c> parameter. Optional defaults / extra constraints /
    /// nullability after the name are tolerated (e.g. <c>{version:apiVersion=1.0}</c>,
    /// <c>{version:apiVersion?}</c>). The whole token is replaced with the concrete version string at
    /// bootstrap, so the constraint never needs to be registered with ASP.NET Core routing.
    /// </summary>
    private static readonly Regex InlineVersionToken = new(
        @"\{\s*(?:apiVersion|[A-Za-z_][A-Za-z0-9_]*\s*:\s*apiVersion)\b[^{}]*\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly WolverineApiVersioningOptions _options;
    private readonly HashSet<HttpChain> _processedChains = new();
    private readonly HashSet<HttpChain> _headerStateChains = new();

    /// <summary>
    /// The pre-rewrite ("logical") route captured for every versioned chain before Step E mutates it.
    /// Used by <see cref="AttachMetadata"/> to group sibling clones after their routes diverge.
    /// </summary>
    private readonly Dictionary<HttpChain, string> _logicalRoutes = new();

    /// <summary>
    /// Chains whose declared route embeds an inline API-version token (see <see cref="InlineVersionToken"/>).
    /// These are rewritten in place and exempt from the URL-segment prefix. Membership is captured once
    /// (from the pre-rewrite route) so it stays stable if <see cref="Apply"/> runs more than once.
    /// </summary>
    private readonly HashSet<HttpChain> _inlineChains = new();

    /// <summary>
    /// Chains for which Step G attached <see cref="ApiVersionEndpointHeaderState"/> metadata, exposed
    /// for <see cref="ApiVersionHeaderFinalizationPolicy"/> to position the writer call at index 0
    /// after all other user-supplied policies have run.
    /// </summary>
    internal IReadOnlySet<HttpChain> ChainsRequiringHeaderEmission => _headerStateChains;

    /// <summary>Initializes a new instance of <see cref="ApiVersioningPolicy"/>.</summary>
    /// <param name="options">The API versioning options that drive this policy's behaviour.</param>
    public ApiVersioningPolicy(WolverineApiVersioningOptions options)
    {
        _options = options;
    }

    /// <inheritdoc/>
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        ResolveAttributes(chains);
        ApplyUnversionedPolicy(chains);
        ApplyOptionsPolicies(chains);
        DetectDuplicateRoutes(chains);
        RewriteRoutes(chains);
        AttachMetadata(chains);
        AttachHeaderState(chains);
    }

    /// <summary>Step A — read <c>[ApiVersion]</c> / <c>[ApiVersionNeutral]</c> from the handler
    /// method and propagate to the chain. Order matters here:
    /// <list type="number">
    ///   <item><description>Check neutrality first so a method-level <c>[ApiVersionNeutral]</c>
    ///     can clear a prior fluent <c>HasApiVersion(...)</c> assignment on the chain
    ///     (test pin: <c>method_level_neutral_clears_prior_fluent_apiversion_assignment</c>).</description></item>
    ///   <item><description>If the chain already carries a version after the neutrality check, it
    ///     came from multi-version expansion or a fluent assignment — keep it as-is. Falling
    ///     through to <see cref="ApiVersionResolver.ResolveVersions"/> on a multi-version method
    ///     would return every declared version and indexing <c>[0]</c> would silently misclassify
    ///     clones.</description></item>
    ///   <item><description>Otherwise resolve from method/class attributes; take the first entry
    ///     after an explicit count check so the <c>default(ApiVersionResolution)</c> foot-gun
    ///     (a struct with a null <c>Version</c>) is not relied on for the empty case.</description></item>
    /// </list></summary>
    private static void ResolveAttributes(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.Method?.Method is null)
                continue;


            // Single reflection pass — resolves neutrality and validates that [ApiVersion] +
            // [ApiVersionNeutral] are not both declared on the same target (throws on conflict).
            // Method-level wins over class-level in both directions. Run this before the
            // already-assigned guard below so a fluent HasApiVersion(...) does not suppress a
            // method-level [ApiVersionNeutral] override. Multi-version clones cannot be neutral
            // (their underlying method declares [ApiVersion]s, so the resolver returns false).
            if (ApiVersionNeutralResolver.Resolve(chain.Method.Method))
            {
                chain.IsApiVersionNeutral = true;
                chain.ApiVersion = null;
                continue;
            }

            // Chains produced by multi-version expansion already have ApiVersion assigned;
            // chains with a fluent HasApiVersion(...) likewise. In both cases the prior assignment
            // wins. Skipping ResolveVersions here also avoids picking versions[0] on a
            // multi-version clone and silently misclassifying it.
            if (chain.ApiVersion is not null)
                continue;

            var versions = ApiVersionResolver.ResolveVersions(chain.Method.Method);
            if (versions.Count == 0)
                continue;

            var resolution = versions[0];
            chain.ApiVersion = resolution.Version;

            if (resolution.IsDeprecated && chain.DeprecationPolicy is null)
                chain.DeprecationPolicy = new DeprecationPolicy();
        }
    }

    /// <summary>Step B — handle chains still missing a version per the configured fallback rule.
    /// Chains carrying <see cref="HttpChain.IsApiVersionNeutral"/> are treated as having made an
    /// explicit version-neutral choice, so they are exempt from <see cref="UnversionedPolicy.RequireExplicit"/>
    /// and <see cref="UnversionedPolicy.AssignDefault"/>.</summary>
    private void ApplyUnversionedPolicy(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is not null || chain.IsApiVersionNeutral)
                continue;

            switch (_options.UnversionedPolicy)
            {
                case UnversionedPolicy.PassThrough:
                    break;

                case UnversionedPolicy.RequireExplicit:
                    throw new InvalidOperationException(
                        $"Endpoint '{Identify(chain)}' does not declare an [ApiVersion] attribute. " +
                        $"The current UnversionedPolicy is '{UnversionedPolicy.RequireExplicit}', which requires every endpoint " +
                        "to carry an explicit version. To opt an endpoint out of versioning, mark it with [ApiVersionNeutral].");

                case UnversionedPolicy.AssignDefault:
                    chain.ApiVersion = _options.DefaultVersion
                        ?? throw new InvalidOperationException(
                            "DefaultVersion must be set when UnversionedPolicy is AssignDefault.");
                    break;
            }
        }
    }

    /// <summary>Step C — apply sunset / deprecation policies from options without overwriting attribute-driven values.</summary>
    private void ApplyOptionsPolicies(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is null)
                continue;

            if (chain.SunsetPolicy is null && _options.SunsetPolicies.TryGetValue(chain.ApiVersion, out var sunset))
                chain.SunsetPolicy = sunset;

            if (chain.DeprecationPolicy is null && _options.DeprecationPolicies.TryGetValue(chain.ApiVersion, out var dep))
                chain.DeprecationPolicy = dep;
        }
    }

    /// <summary>Step D — fail fast when two chains collide. Versioned chains collide on
    /// <c>(verb, route, version)</c>; neutral chains collide on <c>(verb, route)</c> alone, since
    /// they are not partitioned by version. Without this second check, two neutral chains at the
    /// same route would both register and ASP.NET Core would throw an opaque routing error at
    /// the first request.</summary>
    private static void DetectDuplicateRoutes(IReadOnlyList<HttpChain> chains)
    {
        DetectConflicts(
            chains,
            include: c => c.ApiVersion is not null,
            keyOf: c => (
                Verb: c.HttpMethods.FirstOrDefault() ?? "",
                Route: c.RoutePattern?.RawText ?? "",
                Version: c.ApiVersion!.ToString()),
            describe: (key, names) =>
                $"Duplicate endpoint registration detected: " +
                $"[{key.Verb}] '{key.Route}' at version '{key.Version}'. " +
                $"Conflicting chains: {names}");

        DetectConflicts(
            chains,
            include: c => c.IsApiVersionNeutral,
            keyOf: c => (
                Verb: c.HttpMethods.FirstOrDefault() ?? "",
                Route: c.RoutePattern?.RawText ?? ""),
            describe: (key, names) =>
                $"Duplicate version-neutral endpoint registration detected: " +
                $"[{key.Verb}] '{key.Route}'. " +
                $"Version-neutral chains are not partitioned by version, so two chains at the " +
                $"same (verb, route) collide unconditionally. Conflicting chains: {names}");
    }

    private static void DetectConflicts<TKey>(
        IReadOnlyList<HttpChain> chains,
        Func<HttpChain, bool> include,
        Func<HttpChain, TKey> keyOf,
        Func<TKey, string, string> describe)
    {
        var conflicts = chains
            .Where(include)
            .GroupBy(keyOf)
            .Where(g => g.Count() > 1);

        foreach (var conflict in conflicts)
        {
            // Use OperationId here (rather than the shared DisplayName via Identify) so the
            // diagnostic names every conflicting clone individually — the version-suffixed
            // OperationIds make each clone uniquely identifiable when sibling clones across
            // distinct handler classes collide at the same (verb, route, version) triple.
            // Neutral chains likewise have unique OperationIds, so the same naming works for both
            // describe() callers.
            var names = string.Join(", ", conflict.Select(c => c.OperationId));
            throw new InvalidOperationException(describe(conflict.Key, names));
        }
    }

    /// <summary>Step E — resolve where the version lives in the route. A chain whose declared route
    /// already embeds an inline <c>{version:apiVersion}</c> / <c>{apiVersion}</c> token (the ASP.NET
    /// Core convention) has that token substituted in place with the concrete version and is exempt
    /// from the URL-segment prefix — the route is self-describing. Every other versioned chain gets
    /// the configured <see cref="WolverineApiVersioningOptions.UrlSegmentPrefix"/> prepended. Inline
    /// substitution runs regardless of the prefix setting, so it also works in header / query-string
    /// (no-prefix) mode. Because the token is replaced with a literal path segment, every OpenAPI
    /// generator (Microsoft.AspNetCore.OpenApi, NSwag, Swashbuckle) sees a plain versioned path with
    /// no lingering <c>{version}</c> path parameter.</summary>
    private void RewriteRoutes(IReadOnlyList<HttpChain> chains)
    {
        CaptureLogicalRoutes(chains);
        ValidateInlineTokens(chains);
        ValidateUrlSegmentPrefix(chains);

        foreach (var chain in chains)
        {
            if (chain.ApiVersion is null || chain.RoutePattern is null)
                continue;

            // Scenario 3 — the version lives directly in the route template. Substitute and stop:
            // applying the URL-segment prefix on top would double-prefix a self-describing route.
            if (_inlineChains.Contains(chain))
            {
                RewriteInlineVersionToken(chain);
                continue;
            }

            // Scenario 1 — prepend the configured URL-segment prefix.
            // Scenario 2 — UrlSegmentPrefix is null: leave the route untouched (header/query versioning).
            if (_options.UrlSegmentPrefix is not null)
                RewriteRouteForChain(chain);
        }
    }

    /// <summary>Records each versioned chain's pre-rewrite ("logical") route and flags the ones that
    /// carry an inline version token. Sibling clones share the logical route string — for prefix /
    /// no-prefix modes it is the declared route (e.g. <c>/orders</c>); for inline-token routes it
    /// still contains the un-substituted token (identical across every version of that route), so
    /// clones group together in <see cref="AttachMetadata"/> even after their live routes diverge.</summary>
    private void CaptureLogicalRoutes(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is null || chain.RoutePattern is null)
                continue;

            var raw = chain.RoutePattern.RawText ?? string.Empty;

            // TryAdd so a repeated Apply() keeps the true pre-rewrite route captured on the first pass.
            if (_logicalRoutes.TryAdd(chain, raw) && InlineVersionToken.IsMatch(raw))
                _inlineChains.Add(chain);
        }
    }

    /// <summary>Fails fast when a route embeds an inline <c>apiVersion</c> token but no version was
    /// resolved for the chain (unversioned pass-through or <c>[ApiVersionNeutral]</c>). There is no
    /// version to substitute, so the token would reach ASP.NET Core routing verbatim and fault with an
    /// opaque "route constraint 'apiVersion' not found" — a clear startup error is far friendlier.</summary>
    private static void ValidateInlineTokens(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is not null || chain.RoutePattern is null)
                continue;

            var route = chain.RoutePattern.RawText ?? string.Empty;
            if (!InlineVersionToken.IsMatch(route))
                continue;

            throw new InvalidOperationException(
                $"Endpoint '{Identify(chain)}' has a route ('{route}') that embeds an inline API-version " +
                "token but no API version could be resolved for it. Declare a version with [ApiVersion] " +
                "or [MapToApiVersion], or remove the inline version token from the route.");
        }
    }

    /// <summary>Substitutes the inline version token in <paramref name="chain"/>'s route with the
    /// concrete version string from <see cref="WolverineApiVersioningOptions.UrlSegmentVersionFormatter"/>.
    /// Idempotent: once substituted the token is gone, so a second pass finds nothing to replace.</summary>
    private void RewriteInlineVersionToken(HttpChain chain)
    {
        var route = chain.RoutePattern!.RawText ?? string.Empty;
        if (!InlineVersionToken.IsMatch(route))
            return; // already substituted on a previous Apply()

        var versionSegment = _options.UrlSegmentVersionFormatter(chain.ApiVersion!);
        chain.RoutePattern = RoutePatternFactory.Parse(InlineVersionToken.Replace(route, versionSegment));
    }

    private void ValidateUrlSegmentPrefix(IReadOnlyList<HttpChain> chains)
    {
        if (_options.UrlSegmentPrefix is null)
            return;

        if (_options.UrlSegmentPrefix.Contains("{version}", StringComparison.Ordinal))
            return;

        // Only chains that will actually consume the prefix matter — a chain whose route embeds an
        // inline version token is rewritten in place and never touches the prefix. Keyed off the
        // captured inline-chain set so the result stays stable across repeated Apply() calls.
        if (!chains.Any(c => c.ApiVersion is not null && !_inlineChains.Contains(c)))
            return;

        throw new InvalidOperationException(
            $"WolverineApiVersioningOptions.UrlSegmentPrefix is set to '{_options.UrlSegmentPrefix}' which does not contain the required '{{version}}' token. All versioned endpoints would map to the same URL prefix. Set UrlSegmentPrefix to null to disable URL-segment versioning, or include '{{version}}' in the prefix template (e.g. 'v{{version}}' or 'api/v{{version}}').");
    }

    private void RewriteRouteForChain(HttpChain chain)
    {
        var expectedPrefix = BuildExpectedPrefix(chain.ApiVersion!);
        var currentRoute = chain.RoutePattern!.RawText ?? string.Empty;

        // Idempotency guard: skip if the chain is already prefixed.
        if (currentRoute == expectedPrefix ||
            currentRoute.StartsWith(expectedPrefix + "/", StringComparison.Ordinal))
        {
            return;
        }

        var trimmed = currentRoute.TrimStart('/');
        var newRoute = string.IsNullOrEmpty(trimmed) ? expectedPrefix : $"{expectedPrefix}/{trimmed}";
        chain.RoutePattern = RoutePatternFactory.Parse(newRoute);
    }

    private string BuildExpectedPrefix(ApiVersion version)
    {
        var versionSegment = _options.UrlSegmentVersionFormatter(version);
        return "/" + _options.UrlSegmentPrefix!.Replace("{version}", versionSegment).TrimStart('/');
    }

    /// <summary>Step F — attach group-name, ApiVersionMetadata, and ensure unique endpoint names.
    /// Versioned chains' <c>ApiVersionMetadata</c> model is seeded with the union of versions
    /// implemented at the same (verb, route) pair so the <c>api-supported-versions</c> response
    /// header reports every sibling clone, not just this clone's own version. Version-neutral
    /// chains receive <see cref="ApiVersionMetadata.Neutral"/> so consumers of the metadata graph
    /// (Asp.Versioning tooling, the Swashbuckle filter) can recognise them, but they deliberately
    /// get no <c>IEndpointGroupNameMetadata</c>. Without a group name they are skipped by
    /// Swashbuckle's default group-name partitioning; users opt them into versioned documents
    /// from <c>DocInclusionPredicate</c> (see <c>versioning.md</c>).</summary>
    /// <remarks>
    /// The sibling grouping key for versioned chains is <c>(verb, route-after-strip-prefix)</c>,
    /// NOT <c>(verb, route-after-strip-prefix, handler-type)</c>. Chains from distinct handler
    /// classes that publish the same logical route are merged into one sibling set. This matches
    /// the Asp.Versioning convention where any chain at the route is part of the same logical
    /// version set regardless of which class declared which version (e.g.
    /// <c>OrdersV1V2Endpoint</c> declaring v1+v2 and <c>OrdersV3Endpoint</c> declaring v3 at the
    /// same <c>(GET, /orders)</c> route are merged into one sibling chain advertising 1.0/2.0/3.0
    /// in <c>api-supported-versions</c>). The <c>cross_class_chains_at_same_route_share_supported_versions</c>
    /// integration test pins this behaviour.
    /// </remarks>
    private void AttachMetadata(IReadOnlyList<HttpChain> chains)
    {
        // Group versioned chains by (verb, route-without-version-prefix). Two chains in the same
        // group are siblings — typically multi-version clones, but also any chains that happen to
        // share a verb and the post-strip route. Each clone's model advertises the full sibling set
        // as supported / deprecated so the response header consumers see the union.
        var siblingsByKey = new Dictionary<(string Verb, string Route), List<HttpChain>>();
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is null) continue;

            var key = (
                Verb: chain.HttpMethods.FirstOrDefault() ?? "",
                Route: LogicalRoute(chain));

            if (!siblingsByKey.TryGetValue(key, out var bucket))
            {
                bucket = new List<HttpChain>();
                siblingsByKey[key] = bucket;
            }
            bucket.Add(chain);
        }

        foreach (var chain in chains)
        {
            // Mirror ApplyUnversionedPolicy: deal with the neutral branch first so the intent of
            // each branch is obvious. The _processedChains guard then prevents double-attachment
            // of versioned metadata if Apply() is called twice on the same chain.
            if (chain.IsApiVersionNeutral)
            {
                if (!_processedChains.Add(chain))
                    continue;

                chain.Metadata.WithMetadata(ApiVersionMetadata.Neutral);

                // Two neutral chains can share the same handler-method name (e.g. two classes
                // each declaring a method called Get). Without an explicit OperationId, ASP.NET
                // Core derives EndpointName from the route pattern, and two neutral handlers at
                // different routes still hit a duplicate-name collision because the underlying
                // ToString() is not unique per chain. Set the OperationId — already unique per
                // handler type + method — as the explicit endpoint name, just like versioned chains.
                EnsureExplicitOperationId(chain);

                continue;
            }

            if (!_processedChains.Add(chain))
                continue;

            if (chain.ApiVersion is null)
                continue;

            var groupName = _options.OpenApi.DocumentNameStrategy(chain.ApiVersion);
            chain.Metadata.WithGroupName(groupName);

            var key = (
                Verb: chain.HttpMethods.FirstOrDefault() ?? "",
                Route: LogicalRoute(chain));

            var siblings = siblingsByKey[key];
            var supported = siblings
                .Where(s => s.DeprecationPolicy is null)
                .Select(s => s.ApiVersion!)
                .Distinct()
                .ToArray();
            var deprecated = siblings
                .Where(s => s.DeprecationPolicy is not null)
                .Select(s => s.ApiVersion!)
                .Distinct()
                .ToArray();

            var model = new ApiVersionModel(
                declaredVersions: new[] { chain.ApiVersion },
                supportedVersions: supported,
                deprecatedVersions: deprecated,
                advertisedVersions: Array.Empty<ApiVersion>(),
                deprecatedAdvertisedVersions: Array.Empty<ApiVersion>());
            chain.Metadata.WithMetadata(new ApiVersionMetadata(model, model));

            // Make the OperationId (already unique per handler type + method) the explicit
            // endpoint name. Without this, ASP.NET Core uses ToString() which is derived from
            // the original route pattern and collides when multiple versions share the same
            // route template (e.g. [WolverineGet("/orders")] on three different classes).
            EnsureExplicitOperationId(chain);
        }
    }

    /// <summary>Returns the pre-rewrite ("logical") route captured for the chain in
    /// <see cref="CaptureLogicalRoutes"/>. Sibling clones share this value regardless of which
    /// version-placement mode (inline token, URL-segment prefix, or none) rewrote their live route,
    /// so it is the stable key for grouping siblings that publish the same logical endpoint. Falls
    /// back to the current route for any chain that was never captured (defensive; should not occur
    /// for versioned chains).</summary>
    private string LogicalRoute(HttpChain chain)
        => _logicalRoutes.TryGetValue(chain, out var route)
            ? route
            : chain.RoutePattern?.RawText ?? string.Empty;

    private static void EnsureExplicitOperationId(HttpChain chain)
    {
        if (!chain.HasExplicitOperationId)
            chain.SetExplicitOperationId(chain.OperationId);
    }

    /// <summary>
    /// Step G — attach the per-chain <see cref="ApiVersionEndpointHeaderState"/> metadata that the
    /// writer reads at request time. The actual <c>chain.Middleware.Insert(0, …)</c> for the writer
    /// itself is deferred to <see cref="ApiVersionHeaderFinalizationPolicy"/>, which is registered
    /// at the end of <c>MapWolverineEndpoints</c> so it executes after every user-supplied policy
    /// (notably FluentValidation, which itself inserts a short-circuiting frame at index 0). Doing
    /// the insert here would leave the writer below those frames and the OnStarting hook would not
    /// register before <c>return;</c> on the validation-fail path.
    /// </summary>
    private void AttachHeaderState(IReadOnlyList<HttpChain> chains)
    {
        foreach (var chain in chains)
        {
            if (chain.ApiVersion is null || !RequiresHeaderEmission(chain))
                continue;

            if (!_headerStateChains.Add(chain))
                continue;

            // Per-chain state lives on endpoint metadata so the singleton writer can read it at request time.
            var state = new ApiVersionEndpointHeaderState(chain.ApiVersion, chain.SunsetPolicy, chain.DeprecationPolicy);
            chain.Metadata.WithMetadata(state);
        }
    }

    private bool RequiresHeaderEmission(HttpChain chain) =>
        chain.SunsetPolicy is not null
        || chain.DeprecationPolicy is not null
        || _options.EmitApiSupportedVersionsHeader;

    /// <summary>
    /// Diagnostic identifier for a chain in error messages from the unversioned-policy and other
    /// non-clone code paths. Prefers <see cref="HttpChain.DisplayName"/> so consumer-friendly
    /// labels (e.g. <c>"GET /orders (unversioned)"</c>) are preserved verbatim. The duplicate-route
    /// detector in <see cref="DetectDuplicateRoutes"/> intentionally uses
    /// <see cref="HttpChain.OperationId"/> instead because clones share a DisplayName but have
    /// version-suffixed OperationIds.
    /// </summary>
    private static string Identify(HttpChain chain) =>
        chain.DisplayName
        ?? (chain.Method?.Method?.DeclaringType?.FullName + "." + chain.Method?.Method?.Name)
        ?? "(unknown)";
}
