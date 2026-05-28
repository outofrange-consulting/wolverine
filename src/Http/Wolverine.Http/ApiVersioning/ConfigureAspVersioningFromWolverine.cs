using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Bridges <see cref="WolverineApiVersioningOptions"/> into <c>Asp.Versioning.Http</c>'s own
/// <see cref="ApiVersioningOptions"/>. Registered as <see cref="IConfigureOptions{TOptions}"/>
/// so the package picks up Wolverine's source configuration at the moment
/// Microsoft.Extensions.Options resolves its bound instance — which is after
/// <c>MapWolverineEndpoints</c> has invoked the user's <c>UseApiVersioning(...)</c> callback.
/// A static delegate at <c>AddWolverineHttp</c> time would snapshot the pre-configuration state.
/// </summary>
internal sealed class ConfigureAspVersioningFromWolverine : IConfigureOptions<ApiVersioningOptions>
{
    private readonly WolverineHttpOptions _httpOptions;

    public ConfigureAspVersioningFromWolverine(WolverineHttpOptions httpOptions)
    {
        _httpOptions = httpOptions;
    }

    public void Configure(ApiVersioningOptions options)
    {
        var wolverine = _httpOptions.ApiVersioning;
        if (wolverine is null)
        {
            // Application never called UseApiVersioning(...). Make sure the package's default
            // query-string reader does not accidentally pick up unrelated 'api-version' query
            // parameters on routes that have no ApiVersionMetadata.
            options.ApiVersionReader = ZeroApiVersionReader.Instance;
            options.ReportApiVersions = false;
            return;
        }

        var readers = new List<IApiVersionReader>();

        // URL-segment versioning is handled by Wolverine's route rewriter — the version is part
        // of the path literally, not a route parameter — so the package's UrlSegmentApiVersionReader
        // would not find anything. After URL routing has narrowed candidates to one, the matcher
        // policy reads nothing from the request, then falls through to ApiVersionSelector. We set
        // CurrentImplementationApiVersionSelector below so the selector picks the candidate's own
        // declared version (the only one in the per-candidate model at that point) and the matcher
        // policy keeps the candidate valid.

        if (wolverine.VersionHeaderNames.Count > 0)
        {
            readers.Add(new HeaderApiVersionReader(wolverine.VersionHeaderNames.ToArray()));
        }

        if (wolverine.VersionQueryStringNames.Count > 0)
        {
            readers.Add(new QueryStringApiVersionReader(wolverine.VersionQueryStringNames.ToArray()));
        }

        options.ApiVersionReader = readers.Count switch
        {
            0 => ZeroApiVersionReader.Instance,
            1 => readers[0],
            _ => ApiVersionReader.Combine(readers.ToArray()),
        };

        // Selector strategy depends on whether the user wired an explicit DefaultVersion:
        //   - With DefaultVersion set, prefer DefaultApiVersionSelector so the configured version
        //     wins over the package's "pick the latest" heuristic when no version is supplied.
        //   - Without DefaultVersion, fall back to CurrentImplementationApiVersionSelector. The
        //     "current implementation" semantics are what makes the matcher policy interoperate
        //     with Wolverine's URL-segment rewriting: after URL routing has narrowed candidates
        //     to one, the per-candidate model carries that candidate's lone declared version,
        //     and the selector hands it straight back so the matcher keeps the candidate valid.
        if (wolverine.DefaultVersion is not null)
        {
            options.DefaultApiVersion = wolverine.DefaultVersion;
            options.ApiVersionSelector = new DefaultApiVersionSelector(options);
        }
        else
        {
            options.ApiVersionSelector = new CurrentImplementationApiVersionSelector(options);
        }

        // For URL-segment-only mode we MUST force AssumeDefault on, otherwise the matcher policy
        // 400s the request because no IApiVersionReader yielded a value. For header/QS mode we
        // respect the user's opt-in. The selector behaviour above keeps both paths coherent.
        options.AssumeDefaultVersionWhenUnspecified =
            wolverine.AssumeDefaultVersionWhenUnspecified || !wolverine.HasNonUrlVersionSource;

        if (wolverine.UnsupportedApiVersionStatusCode is { } statusCode)
        {
            options.UnsupportedApiVersionStatusCode = statusCode;
        }

        // The package's DefaultApiVersionReporter is intentionally disabled here. The reporter
        // (a) only fires on the error path — UnspecifiedApiVersionEndpoint /
        // UnsupportedApiVersionEndpoint via ApiVersionRequestDelegateExtensions.TryReportApiVersions —
        // not on the success path where the chain pipeline executes, and (b) emits
        // api-supported-versions and api-deprecated-versions as separate, split headers per
        // Asp.Versioning convention. To keep the response shape consistent across success and
        // error paths Wolverine emits both headers itself, merging supported and deprecated into
        // a single api-supported-versions value (the established Wolverine wire format).
        options.ReportApiVersions = false;
    }

    /// <summary>
    /// Sentinel <see cref="IApiVersionReader"/> that always returns an empty result. Used when no
    /// non-URL source is configured so the package's matcher policy treats every request as
    /// "no version supplied" and falls through to the unversioned / default-version branch —
    /// otherwise the package's default (a <see cref="QueryStringApiVersionReader"/> reading the
    /// <c>api-version</c> parameter) would silently start driving selection.
    /// </summary>
    internal sealed class ZeroApiVersionReader : IApiVersionReader
    {
        public static readonly ZeroApiVersionReader Instance = new();

        public IReadOnlyList<string> Read(HttpRequest request) => Array.Empty<string>();

        public void AddParameters(IApiVersionParameterDescriptionContext context)
        {
            // Intentionally empty — there is no parameter to surface in OpenAPI for the
            // sentinel reader, because it reads nothing from the request.
        }
    }
}
