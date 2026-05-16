using Alba;
using Asp.Versioning;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.ApiVersioning;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.ApiVersioning;

// ---------- Handler fixtures local to this file ----------
//
// Must be public for HttpChainSource discovery (it filters on Type.IsPublic). These types live
// only in the test assembly — the production AppFixture run is rooted at WolverineWebApi.Program
// whose Discovery scope explicitly includes its own two assemblies and never scans this one, so
// these endpoints stay quarantined to this test's self-contained Alba host.

[ApiVersion("1.0")]
public static class HeaderOrdersV1Endpoint
{
    [WolverineGet("/hv-orders", OperationId = "HeaderOrdersV1.Get")]
    public static string Get() => "v1";
}

[ApiVersion("2.0")]
public static class HeaderOrdersV2Endpoint
{
    [WolverineGet("/hv-orders", OperationId = "HeaderOrdersV2.Get")]
    public static string Get() => "v2";
}

[ApiVersion("3.0")]
public static class HeaderOrdersV3Endpoint
{
    [WolverineGet("/hv-orders", OperationId = "HeaderOrdersV3.Get")]
    public static string Get() => "v3";
}

[ApiVersionNeutral]
public static class HeaderOrdersHealthEndpoint
{
    [WolverineGet("/hv-health", OperationId = "HeaderOrdersHealth.Get")]
    public static string Get() => "ok";
}

// Multi-version handler — class-level [ApiVersion] declares the full set; expansion
// fans out into per-version clones, each of which keeps the same /hv-customers route
// because UrlSegmentPrefix is null in this test.
[ApiVersion("1.0")]
[ApiVersion("2.0")]
public static class HeaderMultiCustomersEndpoint
{
    [WolverineGet("/hv-customers", OperationId = "HeaderMultiCustomers.Get")]
    public static string Get() => "customers";
}

public class header_versioning_integration_tests : IAsyncDisposable, IDisposable
{
    private IAlbaHost? _host;

    public void Dispose() => _host?.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<IAlbaHost> Build(Action<WolverineApiVersioningOptions> configureVersioning)
    {
        var builder = WebApplication.CreateBuilder([]);

        // Restrict discovery so WolverineWebApi's URL-segmented endpoints (e.g. /v1/orders) are
        // not pulled into this host alongside the header-mode fixtures — otherwise the policy
        // would see two clashing configurations (URL prefix null vs. /v{version}) on the same chains.
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(GetType().Assembly);

            // The test assembly contains other endpoints (Marten aggregates, Newtonsoft-only fixtures,
            // etc.) that get picked up by HttpChainSource. Wire Marten with the same connection string
            // the rest of the test suite uses so codegen for those neighbours can resolve IDocumentStore;
            // we don't actually exercise any of them in this file.
            opts.Services.AddMarten(opts2 =>
            {
                opts2.Connection(Servers.PostgresConnectionString);
                opts2.DisableNpgsqlLogging = true;
            }).IntegrateWithWolverine();
        });

        builder.Services.CritterStackDefaults(opts =>
        {
            opts.Development.GeneratedCodeMode = TypeLoadMode.Auto;
        });

        builder.Services.AddWolverineHttp();

        _host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.UseApiVersioning(v =>
                {
                    // Disable URL-segment versioning — the matcher policy is the only thing
                    // disambiguating clones at /hv-orders.
                    v.UrlSegmentPrefix = null;
                    configureVersioning(v);
                });
            });
        });

        return _host;
    }

    // ---------- Header source ----------

    [Fact]
    public async Task header_selects_v1_when_X_Api_Version_is_1_0()
    {
        var host = await Build(v => v.ReadVersionFromHeader("X-Api-Version"));

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-orders");
            x.WithRequestHeader("X-Api-Version", "1.0");
            x.ContentShouldBe("v1");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task header_selects_v2_when_X_Api_Version_is_2_0()
    {
        var host = await Build(v => v.ReadVersionFromHeader("X-Api-Version"));

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-orders");
            x.WithRequestHeader("X-Api-Version", "2.0");
            x.ContentShouldBe("v2");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task missing_header_returns_404_when_no_default_configured()
    {
        var host = await Build(v => v.ReadVersionFromHeader("X-Api-Version"));

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-orders");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task missing_header_falls_back_to_default_when_AssumeDefault_is_configured()
    {
        var host = await Build(v =>
        {
            v.ReadVersionFromHeader("X-Api-Version");
            v.AssumeDefaultVersionWhenUnspecified = true;
            v.DefaultVersion = new ApiVersion(2, 0);
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-orders");
            x.ContentShouldBe("v2");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task malformed_header_returns_404()
    {
        var host = await Build(v => v.ReadVersionFromHeader("X-Api-Version"));

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-orders");
            x.WithRequestHeader("X-Api-Version", "not-a-real-version");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task unknown_version_returns_404()
    {
        var host = await Build(v => v.ReadVersionFromHeader("X-Api-Version"));

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-orders");
            x.WithRequestHeader("X-Api-Version", "9.9");
            x.StatusCodeShouldBe(404);
        });
    }

    // ---------- Query-string source ----------

    [Fact]
    public async Task query_string_selects_v1()
    {
        var host = await Build(v => v.ReadVersionFromQueryString("api-version"));

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-orders?api-version=1.0");
            x.ContentShouldBe("v1");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task combined_sources_select_the_agreed_version()
    {
        var host = await Build(v =>
        {
            v.ReadVersionFromHeader("X-Api-Version");
            v.ReadVersionFromQueryString("api-version");
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-orders?api-version=2.0");
            x.WithRequestHeader("X-Api-Version", "2.0");
            x.ContentShouldBe("v2");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task disagreeing_sources_invalidate_versioned_candidates()
    {
        var host = await Build(v =>
        {
            v.ReadVersionFromHeader("X-Api-Version");
            v.ReadVersionFromQueryString("api-version");
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-orders?api-version=1.0");
            x.WithRequestHeader("X-Api-Version", "2.0");
            x.StatusCodeShouldBe(404);
        });
    }

    // ---------- Neutral siblings remain untouched ----------

    [Fact]
    public async Task neutral_endpoint_is_routable_with_or_without_header()
    {
        var host = await Build(v => v.ReadVersionFromHeader("X-Api-Version"));

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-health");
            x.ContentShouldBe("ok");
            x.StatusCodeShouldBeOk();
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-health");
            x.WithRequestHeader("X-Api-Version", "1.0");
            x.ContentShouldBe("ok");
            x.StatusCodeShouldBeOk();
        });
    }

    // ---------- Multi-version handler (cloned) ----------

    [Fact]
    public async Task multi_version_handler_clones_route_to_header_value()
    {
        var host = await Build(v => v.ReadVersionFromHeader("X-Api-Version"));

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-customers");
            x.WithRequestHeader("X-Api-Version", "2.0");
            x.ContentShouldBe("customers");
            x.StatusCodeShouldBeOk();
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/hv-customers");
            x.WithRequestHeader("X-Api-Version", "1.0");
            x.ContentShouldBe("customers");
            x.StatusCodeShouldBeOk();
        });
    }

    // ---------- Response headers stay coherent in header mode ----------

    [Fact]
    public async Task api_supported_versions_header_lists_siblings_in_header_mode()
    {
        var host = await Build(v => v.ReadVersionFromHeader("X-Api-Version"));

        var result = await host.Scenario(x =>
        {
            x.Get.Url("/hv-orders");
            x.WithRequestHeader("X-Api-Version", "1.0");
            x.StatusCodeShouldBeOk();
        });

        var header = result.Context.Response.Headers["api-supported-versions"].FirstOrDefault();
        header.ShouldNotBeNull();
        header.ShouldBe("1.0, 2.0, 3.0");
    }
}
