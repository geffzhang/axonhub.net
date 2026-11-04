using HotChocolate;
using HotChocolate.Execution;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of xerrors.CodedError (internal/pkg/xerrors/graphql.go:37-56). The
/// GraphQL error presenter reports <see cref="Exception.Message"/> and the code
/// in <c>extensions.code</c>; any resolver wrapper prefix is dropped.
/// </summary>
public sealed class CodedException : Exception
{
    /// <summary>xerrors.ErrCodeValidationFailed.</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>xerrors.ErrCodeForbidden.</summary>
    public const string Forbidden = "FORBIDDEN";

    /// <summary>xerrors.ErrCodeNotFound.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>xerrors.ErrCodeDuplicateName.</summary>
    public const string DuplicateName = "DUPLICATE_NAME";

    public CodedException(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null)
        : base(message)
    {
        Code = code;
        Extensions = extensions ?? new Dictionary<string, object?>();
    }

    public string Code { get; }

    /// <summary>
    /// The reference always serializes the resource/field/value keys, even when
    /// they are nil, so they are exposed as nullable entries.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Extensions { get; }
}

/// <summary>
/// Port of the gqlgen error presenter for CodedError
/// (internal/server/gql/graphql.go:153-179).
/// </summary>
public sealed class AxonHubGraphQLErrorFilter : IErrorFilter
{
    public IError OnError(IError error)
    {
        var coded = FindCoded(error.Exception);
        if (coded is null)
        {
            // Port of the privacy.Deny branch (graphql.go:181-190): any error
            // wrapping the sentinel becomes "permission denied" with only the
            // FORBIDDEN code in its extensions. The presenter checks the coded
            // error first, exactly like the reference.
            if (FindPrivacyDeny(error.Exception) is not null)
            {
                return ErrorBuilder.FromError(error)
                    .SetMessage("permission denied")
                    .SetExtension("code", CodedException.Forbidden)
                    .Build();
            }

            // graphql.DefaultErrorPresenter reports err.Error() for every
            // resolver error; HotChocolate would otherwise hide the message
            // behind "Unexpected Execution Error".
            return error.Exception is null
                ? error
                : ErrorBuilder.FromError(error).SetMessage(error.Exception.Message).Build();
        }

        var builder = ErrorBuilder.FromError(error)
            .SetMessage(coded.Message)
            .SetExtension("code", coded.Code)
            .SetExtension(
                "resource",
                coded.Extensions.TryGetValue("resource", out var resource) ? resource : null)
            .SetExtension(
                "field",
                coded.Extensions.TryGetValue("field", out var field) ? field : null)
            .SetExtension(
                "value",
                coded.Extensions.TryGetValue("value", out var value) ? value : null);

        return builder.Build();
    }

    private static CodedException? FindCoded(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is CodedException coded)
            {
                return coded;
            }
        }

        return null;
    }

    private static AxonHub.Api.Management.PrivacyDenyException? FindPrivacyDeny(
        Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AxonHub.Api.Management.PrivacyDenyException deny)
            {
                return deny;
            }
        }

        return null;
    }
}
