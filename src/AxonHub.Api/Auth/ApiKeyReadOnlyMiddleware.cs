using System.Text.Json;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of the apiKeyReadOnly AroundOperations hook
/// (internal/server/gql/graphql.go:187-204): service-account API keys may run
/// queries on the admin GraphQL surface, but every mutation and subscription
/// is refused. The reference installs this as a gqlgen operation wrapper
/// (after parse and validation); the port extracts the `query` document from
/// the JSON envelope and parses it ahead of MapGraphQL with HotChocolate's own
/// parser, then answers the reference's one-shot error response.
/// </summary>
public sealed class ApiKeyReadOnlyMiddleware(RequestDelegate next)
{
    /// <summary>GraphQL request-error body: message only, no data.</summary>
    public const string ReadOnlyError = "service account API keys are read-only";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Items[AdminGraphqlIdentity.ContextKey]
            is not AdminGraphqlIdentity { IsApiKey: true })
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // gqlgen only dispatches JSON POSTs; other content types are rejected
        // by its transport before the operation wrapper can run. The port lets
        // HotChocolate handle non-JSON bodies (C109).
        var contentType = context.Request.ContentType;
        if (contentType is null
            || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(
                   context.Request.Body,
                   System.Text.Encoding.UTF8,
                   detectEncodingFromByteOrderMarks: false,
                   leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        context.Request.Body.Position = 0;

        if (!TryReadEnvelope(body, out var query, out var operationName))
        {
            // Not a GraphQL-over-HTTP JSON envelope: gqlgen's transport would
            // reject it before the operation wrapper runs; HotChocolate answers
            // the same request its own way (C109).
            await next(context).ConfigureAwait(false);
            return;
        }

        DocumentNode document;
        try
        {
            document = Utf8GraphQLParser.Parse(query);
        }
        catch (SyntaxException)
        {
            // gqlgen reports parse errors before the operation wrapper runs;
            // HotChocolate reports the same request its own way (C109).
            await next(context).ConfigureAwait(false);
            return;
        }

        var operation = SelectOperation(document, operationName);
        if (operation is null || operation.Operation == OperationType.Query)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        await WriteReadOnlyAsync(context).ConfigureAwait(false);
    }

    private static bool TryReadEnvelope(
        string body,
        out string query,
        out string? operationName)
    {
        query = string.Empty;
        operationName = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("query", out var queryElement)
                || queryElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            query = queryElement.GetString()!;
            if (root.TryGetProperty("operationName", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String)
            {
                operationName = nameElement.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            // Not a JSON envelope: let HotChocolate answer.
            return false;
        }
    }

    private static OperationDefinitionNode? SelectOperation(
        DocumentNode document,
        string? operationName)
    {
        var operations = document.Definitions
            .OfType<OperationDefinitionNode>()
            .ToList();

        if (operationName is not null)
        {
            return operations.FirstOrDefault(operation =>
                string.Equals(operation.Name?.Value, operationName, StringComparison.Ordinal));
        }

        // gqlgen requires an operation name for multi-operation documents
        // before the wrapper runs; a document without operations is a
        // validation error. Both flow through to HotChocolate.
        return operations.Count == 1 ? operations[0] : null;
    }

    private static async Task WriteReadOnlyAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        context.Response.ContentType = "application/graphql-response+json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new { errors = new[] { new { message = ReadOnlyError } } },
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }
}
