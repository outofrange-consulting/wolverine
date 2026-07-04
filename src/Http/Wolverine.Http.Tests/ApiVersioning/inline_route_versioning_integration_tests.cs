using System.Text.Json;
using Alba;
using Shouldly;
using WolverineWebApi.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

// End-to-end coverage for "scenario 3" against the real WolverineWebApi host + Swashbuckle. The
// InlineVersionedOrdersEndpoint declares its version inline in the route template
// (/api/inline/v{apiVersion}/orders); Wolverine substitutes the token at bootstrap so the live
// routes are /api/inline/v1/orders and /api/inline/v2/orders, and Swashbuckle documents them as
// plain literal paths.
[Collection("integration")]
public class inline_route_versioning_integration_tests : IntegrationContext
{
    public inline_route_versioning_integration_tests(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task inline_v1_route_is_live()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/api/inline/v1/orders");
            x.StatusCodeShouldBeOk();
        });

        var response = await result.ReadAsJsonAsync<InlineOrdersResponse>();
        response.ShouldNotBeNull();
        response.Orders.ShouldContain("inline-order-1");
    }

    [Fact]
    public async Task inline_v2_route_is_live()
    {
        await Scenario(x =>
        {
            x.Get.Url("/api/inline/v2/orders");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task the_un_substituted_token_route_is_not_registered()
    {
        // Proves the token was substituted rather than left as a live parameterized route.
        await Scenario(x =>
        {
            x.Get.Url("/api/inline/v3/orders");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task swagger_v1_document_contains_the_substituted_path()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/swagger/v1/swagger.json");
            x.StatusCodeShouldBeOk();
        });

        var body = await result.ReadAsTextAsync();
        body.ShouldContain("/api/inline/v1/orders");

        // The substituted path is a literal — no {version}/{apiVersion} parameter leaks into the doc,
        // which is exactly what makes every OpenAPI generator produce the correct output.
        using var doc = JsonDocument.Parse(body);
        var paths = doc.RootElement.GetProperty("paths");
        paths.TryGetProperty("/api/inline/v1/orders", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task swagger_v2_document_contains_the_substituted_path()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/swagger/v2/swagger.json");
            x.StatusCodeShouldBeOk();
        });

        var body = await result.ReadAsTextAsync();
        body.ShouldContain("/api/inline/v2/orders");
    }
}
