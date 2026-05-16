using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// ASP.NET Core <see cref="MatcherPolicy"/> that resolves the requested API version from
/// configured non-URL sources (headers and / or query string) and filters candidates so that only
/// the endpoint declaring that version stays valid.
///
/// <para>
/// Versioning by URL segment does not need this policy because every version lives at a distinct
/// route — the URL itself disambiguates. When two or more clones share the same route (i.e. when
/// <see cref="WolverineApiVersioningOptions.UrlSegmentPrefix"/> is <see langword="null"/>) the
/// candidate set returned by routing contains every sibling clone; this policy is the piece that
/// then picks the right one based on the request.
/// </para>
///
/// <para>
/// Version-neutral endpoints (those with <see cref="IApiVersionNeutral"/> metadata) are left
/// untouched: they have no declared version, so they always remain valid candidates. Endpoints that
/// have no <see cref="ApiVersionMetadata"/> at all are also left alone — they are not part of any
/// versioned set and are evaluated by the rest of the routing pipeline as usual.
/// </para>
/// </summary>
internal sealed class ApiVersionEndpointSelectorPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    private readonly WolverineApiVersioningOptions _options;

    public ApiVersionEndpointSelectorPolicy(WolverineApiVersioningOptions options)
    {
        _options = options;
    }

    // Must run before the framework's default endpoint selector (order int.MaxValue) and after
    // HttpMethodMatcherPolicy (order 0). We share the same band as the content-type selector to
    // keep ordering predictable across Wolverine's matcher policies.
    public override int Order => 200;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        // Activate the policy as soon as one candidate carries explicit version metadata. The
        // neutral metadata sentinel also reports IsApiVersionNeutral = true; we still want this
        // policy active so we can leave neutral endpoints alone while filtering versioned siblings.
        for (var i = 0; i < endpoints.Count; i++)
        {
            var meta = endpoints[i].Metadata.GetMetadata<ApiVersionMetadata>();
            if (meta is not null && !meta.IsApiVersionNeutral)
                return true;
        }

        return false;
    }

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        // When no non-URL source is configured the policy has nothing to read at request time —
        // URL-segment routing already picked one candidate per (verb, route), so leave the set as-is.
        if (_options.VersionHeaderNames.Count == 0 && _options.VersionQueryStringNames.Count == 0)
            return Task.CompletedTask;

        var read = ApiVersionRequestReader.Read(httpContext.Request, _options);

        switch (read.Status)
        {
            case ApiVersionRequestStatus.Ambiguous:
                // Two configured sources disagreed; fail closed by invalidating every versioned
                // candidate. The framework will then resolve to a 404 — application middleware
                // can map that to a 400 by inspecting the resulting status code if desired.
                InvalidateVersionedCandidates(candidates);
                return Task.CompletedTask;

            case ApiVersionRequestStatus.Malformed:
                InvalidateVersionedCandidates(candidates);
                return Task.CompletedTask;

            case ApiVersionRequestStatus.NotSupplied:
                {
                    var fallback = _options.AssumeDefaultVersionWhenUnspecified
                        ? _options.DefaultVersion
                        : null;

                    if (fallback is null)
                    {
                        // No version supplied and no fallback — invalidate every versioned candidate
                        // so unversioned siblings (if any) win, otherwise the request falls through
                        // to 404. This mirrors Asp.Versioning's default behaviour.
                        InvalidateVersionedCandidates(candidates);
                        return Task.CompletedTask;
                    }

                    KeepOnlyMatchingCandidates(candidates, fallback);
                    return Task.CompletedTask;
                }

            case ApiVersionRequestStatus.Supplied:
                KeepOnlyMatchingCandidates(candidates, read.Version!);
                return Task.CompletedTask;

            default:
                return Task.CompletedTask;
        }
    }

    private static void InvalidateVersionedCandidates(CandidateSet candidates)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i)) continue;

            var meta = candidates[i].Endpoint?.Metadata.GetMetadata<ApiVersionMetadata>();
            if (meta is null) continue;
            if (meta.IsApiVersionNeutral) continue;

            candidates.SetValidity(i, false);
        }
    }

    private static void KeepOnlyMatchingCandidates(CandidateSet candidates, ApiVersion requested)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i)) continue;

            var meta = candidates[i].Endpoint?.Metadata.GetMetadata<ApiVersionMetadata>();
            if (meta is null) continue;
            if (meta.IsApiVersionNeutral) continue;

            // DeclaredApiVersions on the explicit model is the per-clone version set populated by
            // ApiVersioningPolicy. The sibling union goes into ImplementedApiVersions, which is the
            // right input for the response header but the wrong one for selection.
            var explicitModel = meta.Map(ApiVersionMapping.Explicit);
            var declared = explicitModel.DeclaredApiVersions;
            var matched = false;
            for (var d = 0; d < declared.Count; d++)
            {
                if (declared[d] == requested)
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
                candidates.SetValidity(i, false);
        }
    }
}
