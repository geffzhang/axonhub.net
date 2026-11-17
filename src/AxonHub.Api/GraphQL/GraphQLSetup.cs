using AxonHub.Api.Management;
using HotChocolate.AspNetCore;
using HotChocolate.AspNetCore.Formatters;
using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// HotChocolate wiring for the management GraphQL surface. The frontend posts to
/// /admin/graphql (frontend/src/gql/graphql.ts:36); the schema mirrors the
/// gqlgen SDL under internal/server/gql.
/// </summary>
public static class GraphQLSetup
{
    public static IRequestExecutorBuilder AddAxonHubGraphQL(this IServiceCollection services)
    {
        // Bodies and chunks are loaded through the request's data storage
        // (request.go loaders, C020), so the read side needs the same seam the
        // recorder uses.
        services.AddScoped<RequestPayloadStorage>();

        return services.AddGraphQLServer()
            .AddQueryType<AdminQuery>()
            .AddMutationType<AdminMutation>()
            .AddType<CursorType>()
            .AddType<GoTimeType>()
            .AddType<MapScalarType>()
            .AddType<ChannelTagsMatchModeType>()
            .AddType<GoDecimalType>()
            .AddType<JsonRawMessageType>()

            // backup.graphql declares `scalar Upload` for the restore mutation.
            .AddUploadType()

            // Defined by dashboard.graphql but never reached from a root field;
            // registering it keeps the same named type in the schema.
            .AddType<HourlyRequestStatsType>()

            // Node implementors that are not reachable from any root field or
            // connection (node(id:) is their only entry point); HotChocolate
            // only discovers interface implementors among registered types.
            .AddType<ChannelProbeType>()
            .AddType<UserRoleType>()

            // HotChocolate infers input-object names from the CLR type; the
            // gqlgen SDL calls these ChannelOrderingItem, BulkImportChannelItem
            // and AnalyticsFilter. Registering the descriptors explicitly fixes
            // the wire names (axonhub.graphql:409, 445; analytics.graphql:7);
            // the CLR suffix is only there to avoid clashing with a biz type.
            .AddType(new InputObjectType<ChannelOrderingItemInput>(
                descriptor => descriptor.Name("ChannelOrderingItem")))
            .AddType(new InputObjectType<BulkImportChannelItemInput>(
                descriptor => descriptor.Name("BulkImportChannelItem")))
            .AddType(new InputObjectType<AnalyticsFilterInput>(
                descriptor => descriptor.Name("AnalyticsFilter")))
            .AddTypeExtension<ProjectTypeExtensions>()
            .AddTypeExtension<UserTypeExtensions>()
            .AddTypeExtension<ApiKeyTypeExtensions>()
            .AddTypeExtension<ChannelTypeExtensions>()
            .AddTypeExtension<DataStorageTypeExtensions>()
            .AddTypeExtension<RoleTypeExtensions>()
            .AddTypeExtension<ModelTypeExtensions>()
            .AddTypeExtension<ChannelModelPriceTypeExtensions>()
            .AddTypeExtension<ChannelModelPriceVersionTypeExtensions>()
            .AddErrorFilter<AxonHubGraphQLErrorFilter>()
            .AddHttpRequestInterceptor<IntrospectionRequestInterceptor>()
            .AddHttpResponseFormatter(new HttpResponseFormatterOptions
            {
                HttpTransportVersion = HttpTransportVersion.Legacy,
            });
    }
}

/// <summary>
/// gqlgen serves introspection by default; HotChocolate 16 denies it unless the
/// request opts in (HC0046 "Introspection is not allowed for the current
/// request"). The reference schema is discoverable, so every request opts in.
/// </summary>
/// <remarks>
/// Derives from <see cref="DefaultHttpRequestInterceptor"/> because replacing
/// the interceptor also replaces its work: copying the multipart
/// <c>IFileLookup</c> feature onto the operation request (what the Upload
/// scalar resolves), plus HttpContext/ClaimsPrincipal features. Without the
/// base call, restore(file:) fails with "specified file could not be found".
/// </remarks>
internal sealed class IntrospectionRequestInterceptor : DefaultHttpRequestInterceptor
{
    public override ValueTask OnCreateAsync(
        HttpContext context,
        IRequestExecutor requestExecutor,
        OperationRequestBuilder requestBuilder,
        CancellationToken cancellationToken)
    {
        requestBuilder.AllowIntrospection();
        return base.OnCreateAsync(
            context,
            requestExecutor,
            requestBuilder,
            cancellationToken);
    }
}
