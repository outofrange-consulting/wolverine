using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Tests.DifferentAssembly;

namespace Wolverine.Http.Tests.ApiVersioning;

// Verifies that the OFFICIAL Microsoft.AspNetCore.OpenApi generator (the same one the Wolverine
// `openapi` command drives via AddOpenApi()) emits the correct document for "scenario 3" — a route
// whose version token is embedded directly in the template. Because Wolverine substitutes the token
// with the concrete version at bootstrap, the generated document must contain the literal versioned
// path (/api/diff-inline/v1/orders) with no lingering {version}/{apiVersion} path parameter.
//
// Runs entirely from endpoint metadata with no host start and no database, so it needs no docker.
public class inline_route_versioning_microsoft_openapi_tests
{
    private static async Task<string> GenerateAsync(string documentName)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Host.UseWolverine(opts =>
        {
            // Pin discovery to the small isolated assembly so only the inline-versioned endpoint (and
            // its DifferentAssembly siblings) are considered — the test stays deterministic.
            opts.ApplicationAssembly = typeof(InlineVersionedEndpoint).Assembly;
        });

        // One Microsoft.AspNetCore.OpenApi document per version. The default document filter includes
        // endpoints whose group name matches the document name (Wolverine sets the group name to
        // "v1"/"v2" for each versioned clone).
        builder.Services.AddOpenApi("v1");
        builder.Services.AddOpenApi("v2");
        builder.Services.AddWolverineHttp();

        await using var app = builder.Build();
        app.MapWolverineEndpoints(o => o.UseApiVersioning(_ => { }));

        var documentProvider = OpenApiCommand.PrepareDocumentProvider(app);
        documentProvider.ShouldNotBeNull();

        var writer = new StringWriter();
        await documentProvider!.GenerateAsync(documentName, writer);
        return writer.ToString();
    }

    [Fact]
    public async Task v1_document_contains_the_substituted_literal_path()
    {
        var json = await GenerateAsync("v1");

        json.ShouldContain("/api/diff-inline/v1/orders");

        // The version token must be gone — no {version}/{apiVersion} path parameter survives.
        json.ShouldNotContain("{version}");
        json.ShouldNotContain("{apiVersion}");
        json.ShouldNotContain("/api/diff-inline/v2/orders");
    }

    [Fact]
    public async Task v2_document_contains_the_substituted_literal_path()
    {
        var json = await GenerateAsync("v2");

        json.ShouldContain("/api/diff-inline/v2/orders");
        json.ShouldNotContain("{apiVersion}");
        json.ShouldNotContain("/api/diff-inline/v1/orders");
    }
}
