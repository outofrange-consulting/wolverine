using Asp.Versioning;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Outcome of reading the requested API version from a single HTTP request. Modeled as a discriminated
/// outcome so the matcher policy can distinguish "no version supplied" (which then falls through to
/// <see cref="WolverineApiVersioningOptions.AssumeDefaultVersionWhenUnspecified"/>) from "ambiguous"
/// (two sources supplied different version strings) and "malformed" (a supplied string failed to parse).
/// </summary>
internal enum ApiVersionRequestStatus
{
    /// <summary>No configured source produced a value — selection defers to the default version policy.</summary>
    NotSupplied,

    /// <summary>Exactly one version value was supplied (or multiple sources agreed) and it parsed.</summary>
    Supplied,

    /// <summary>Two or more configured sources supplied non-identical version strings.</summary>
    Ambiguous,

    /// <summary>A version string was supplied but could not be parsed.</summary>
    Malformed,
}

/// <summary>
/// Result returned by <see cref="ApiVersionRequestReader.Read"/>. Exposes the parsed version when
/// <see cref="Status"/> is <see cref="ApiVersionRequestStatus.Supplied"/>, the raw text otherwise.
/// </summary>
internal readonly record struct ApiVersionRequestResult(
    ApiVersionRequestStatus Status,
    ApiVersion? Version,
    string? RawValue,
    string? ConflictingValue);

/// <summary>
/// Reads the requested API version from configured non-URL sources (headers and / or query string).
/// URL-segment versioning is handled exclusively by route matching, not by this reader.
/// </summary>
internal static class ApiVersionRequestReader
{
    public static ApiVersionRequestResult Read(HttpRequest request, WolverineApiVersioningOptions options)
    {
        string? first = null;
        var headers = request.Headers;
        var headerNames = options.VersionHeaderNames;
        for (var i = 0; i < headerNames.Count; i++)
        {
            if (!headers.TryGetValue(headerNames[i], out var values)) continue;
            for (var v = 0; v < values.Count; v++)
            {
                var value = values[v];
                if (string.IsNullOrWhiteSpace(value)) continue;

                if (first is null)
                {
                    first = value;
                }
                else if (!string.Equals(first, value, StringComparison.OrdinalIgnoreCase))
                {
                    return new ApiVersionRequestResult(ApiVersionRequestStatus.Ambiguous, null, first, value);
                }
            }
        }

        var query = request.Query;
        var queryNames = options.VersionQueryStringNames;
        for (var i = 0; i < queryNames.Count; i++)
        {
            if (!query.TryGetValue(queryNames[i], out var values)) continue;
            for (var v = 0; v < values.Count; v++)
            {
                var value = values[v];
                if (string.IsNullOrWhiteSpace(value)) continue;

                if (first is null)
                {
                    first = value;
                }
                else if (!string.Equals(first, value, StringComparison.OrdinalIgnoreCase))
                {
                    return new ApiVersionRequestResult(ApiVersionRequestStatus.Ambiguous, null, first, value);
                }
            }
        }

        if (first is null)
            return new ApiVersionRequestResult(ApiVersionRequestStatus.NotSupplied, null, null, null);

        return ApiVersionParser.Default.TryParse(first, out var parsed)
            ? new ApiVersionRequestResult(ApiVersionRequestStatus.Supplied, parsed, first, null)
            : new ApiVersionRequestResult(ApiVersionRequestStatus.Malformed, null, first, null);
    }
}
