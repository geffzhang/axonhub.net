using HotChocolate.AspNetCore;
using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// HotChocolate wiring for the management GraphQL surface. The frontend posts to
/// /admin/graphql (frontend/src/gql/graphql.ts:36); the schema mirrors the
/// gqlgen SDL under internal/server/gql.
/// </summary>
public static class GraphQLSetup
{
    public static IRequestExecutorBuilder AddAxonHubGraphQL(this IServiceCollection services)
        => services.AddGraphQLServer()
            .AddQueryType<AdminQuery>()
            .AddMutationType<AdminMutation>()
            .AddType<CursorType>()
            .AddType<GoTimeType>()
            .AddType<MapScalarType>()
            .AddErrorFilter<AxonHubGraphQLErrorFilter>()
            .AddHttpRequestInterceptor<IntrospectionRequestInterceptor>();
}

/// <summary>
/// gqlgen serves introspection by default; HotChocolate 16 denies it unless the
/// request opts in (HC0046 "Introspection is not allowed for the current
/// request"). The reference schema is discoverable, so every request opts in.
/// </summary>
internal sealed class IntrospectionRequestInterceptor : IHttpRequestInterceptor
{
    public ValueTask OnCreateAsync(
        HttpContext context,
        IRequestExecutor requestExecutor,
        OperationRequestBuilder requestBuilder,
        CancellationToken cancellationToken)
    {
        requestBuilder.AllowIntrospection();
        return default;
    }
}
