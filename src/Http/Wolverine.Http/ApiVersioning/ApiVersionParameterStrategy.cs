using System.Reflection;
using Asp.Versioning;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Binds a handler-method parameter of type <see cref="ApiVersion"/> to the version
/// <c>Asp.Versioning.Http</c>'s <see cref="ApiVersionMatcherPolicy"/> resolved for the request.
/// This lets a Wolverine HTTP endpoint accept the negotiated version directly:
/// <code>
/// [WolverineGet("/orders")]
/// public static OrdersResponse Get(ApiVersion version) => ...;
/// </code>
/// The value is read from <see cref="IApiVersioningFeature.RequestedApiVersion"/> — the same
/// feature the package populates during routing — rather than re-parsing the request, so URL,
/// header, and query-string sources all surface through one parameter. The feature accessor is
/// the cross-version-stable entry point (the v8 <c>HttpContext.GetRequestedApiVersion()</c>
/// extension was removed in v10).
/// </summary>
internal sealed class ApiVersionParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.ParameterType == typeof(ApiVersion))
        {
            variable = new ApiVersionFrame().Version;
            return true;
        }

        variable = null;
        return false;
    }
}

/// <summary>
/// Emits <c>var apiVersion = httpContext.Features.Get&lt;IApiVersioningFeature&gt;()?.RequestedApiVersion;</c>.
/// Fully-qualified type names are used so the generated source needs no extra using directives.
/// </summary>
internal sealed class ApiVersionFrame : SyncFrame
{
    public ApiVersionFrame()
    {
        Version = new Variable(typeof(ApiVersion), this);
    }

    public Variable Version { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Resolved by Asp.Versioning.Http's ApiVersionMatcherPolicy during routing.");
        writer.WriteLine(
            $"var {Version.Usage} = httpContext.Features.Get<{typeof(IApiVersioningFeature).FullNameInCode()}>()?.{nameof(IApiVersioningFeature.RequestedApiVersion)};");

        Next?.GenerateCode(method, writer);
    }
}
