using Asp.Versioning;

namespace Wolverine.Http.Tests.DifferentAssembly;

/// <summary>
/// Isolated endpoint used by the self-contained Microsoft.AspNetCore.OpenApi generation test. The
/// API version is embedded directly in the route template ("scenario 3"); with versioning enabled
/// Wolverine substitutes the token, producing the literal routes /api/diff-inline/v1/orders and
/// /api/diff-inline/v2/orders. The bare {apiVersion} token is used so the route still parses as an
/// ordinary parameter in hosts that never enable versioning.
/// </summary>
[ApiVersion("1.0")]
[ApiVersion("2.0")]
public static class InlineVersionedEndpoint
{
    [WolverineGet("/api/diff-inline/v{apiVersion}/orders", OperationId = "InlineVersionedEndpoint.Get")]
    public static InlineDiffResponse Get() => new("ok");
}

public record InlineDiffResponse(string Status);
