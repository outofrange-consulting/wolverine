using Asp.Versioning;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

/// <summary>
/// Demonstrates "scenario 3": the API version lives directly in the route template rather than being
/// injected by the config-level <c>UrlSegmentPrefix</c>. Wolverine substitutes the inline version
/// token with the concrete version at bootstrap, so this single class publishes the live routes
/// <c>/api/inline/v1/orders</c> and <c>/api/inline/v2/orders</c>. Because the substituted route is a
/// plain literal path with no <c>{version}</c> parameter, the official Microsoft.AspNetCore.OpenApi,
/// NSwag, and Swashbuckle generators all emit those concrete versioned paths.
/// </summary>
/// <remarks>
/// The bare <c>{apiVersion}</c> token is used here (rather than the constraint form
/// <c>{version:apiVersion}</c>) because <c>WolverineWebApi</c> is also loaded by tests that do NOT
/// call <c>UseApiVersioning()</c>. In that mode the versioning policy never runs and the token is
/// left in the route; a bare parameter still parses as an ordinary route parameter, whereas the
/// <c>:apiVersion</c> constraint would require the <c>Asp.Versioning.Http</c> route constraint to be
/// registered. When versioning IS enabled, both token forms behave identically.
/// </remarks>
[ApiVersion("1.0")]
[ApiVersion("2.0")]
public static class InlineVersionedOrdersEndpoint
{
    [WolverineGet("/api/inline/v{apiVersion}/orders", OperationId = "InlineVersionedOrdersEndpoint.Get")]
    public static InlineOrdersResponse Get() => new(["inline-order-1", "inline-order-2"]);
}

public record InlineOrdersResponse(IReadOnlyList<string> Orders);
