using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// HotChocolate wiring for the OpenAPI GraphQL surface. The reference mounts a
/// second, independent gqlgen server at POST /openapi/v1/graphql
/// (internal/server/gql/openapi/graphql.go), so the port hosts it as a named
/// HotChocolate schema in the same process.
/// </summary>
public static class OpenApiGraphQLSetup
{
    /// <summary>
    /// gqlgen's transports accept POST bodies up to the server default; the
    /// port keeps HotChocolate's own default limit explicitly.
    /// </summary>
    private const int MaxAllowedRequestSize = 20 * 1024 * 1024;

    public static IRequestExecutorBuilder AddAxonHubOpenApiGraphQL(
        this IServiceCollection services)
    {
        return services
            .AddGraphQLServer(
                "openapi",
                MaxAllowedRequestSize,
                disableDefaultSecurity: false)
            // HotChocolate names operation types after the CLR class by
            // default (OpenApiQuery/OpenApiMutation); the gqlgen SDL and its
            // introspection expose the roots as Query/Mutation.
            .AddQueryType<OpenApiQuery>(descriptor => descriptor.Name("Query"))
            .AddMutationType<OpenApiMutation>(descriptor => descriptor.Name("Mutation"))
            .AddType<GoTimeType>()
            .AddType<ChannelTagsMatchModeType>()
            .AddType<GoDecimalType>()
            .AddType<GoDecimalInputType>()
            // ModelMapping/ModelMappingInput are reachable through the profile
            // types; registering them again makes HotChocolate 16 reject the
            // duplicate input type name.
            .AddErrorFilter<OpenApiGraphQLErrorFilter>()
            .AddHttpRequestInterceptor<IntrospectionRequestInterceptor>();
    }
}

/// <summary>
/// Port of gqlgen's default error presenter for the OpenAPI schema: the
/// reference installs no custom presenter (openapi/graphql.go), so every
/// resolver error is reported with <c>err.Error()</c> as its message and no
/// extensions. In particular a privacy deny surfaces as
/// <c>ent/privacy: deny rule</c>, not the admin surface's "permission denied".
/// </summary>
public sealed class OpenApiGraphQLErrorFilter : IErrorFilter
{
    public IError OnError(IError error)
        => error.Exception is null
            ? error
            : ErrorBuilder.FromError(error)
                .SetMessage(error.Exception.Message)
                // gqlgen's default presenter emits only message/path/locations;
                // drop HotChocolate's code/extensions so a CodedError or a
                // privacy deny carries no metadata on this schema either.
                .SetCode(null!)
                .ClearExtensions()
                .Build();
}
