using HotChocolate;
using HotChocolate.Types;
using HotChocolate.Types.Relay;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the DataStorageType enum (ent.graphql:2203-2209).</summary>
/// <remarks>The SDL spells the values in lower case (they are the Go enum literals).</remarks>
[GraphQLName("DataStorageType")]
public enum DataStorageTypeValue
{
    [GraphQLName("database")]
    Database,

    [GraphQLName("fs")]
    Fs,

    [GraphQLName("s3")]
    S3,

    [GraphQLName("gcs")]
    Gcs,

    [GraphQLName("webdav")]
    WebDav,
}

/// <summary>Port of the DataStorageStatus enum (ent.graphql:2196-2199).</summary>
[GraphQLName("DataStorageStatus")]
public enum DataStorageStatusValue
{
    [GraphQLName("active")]
    Active,

    [GraphQLName("archived")]
    Archived,
}

/// <summary>Port of DataStorageOrderField (ent.graphql:2189-2192).</summary>
[GraphQLName("DataStorageOrderField")]
public enum DataStorageOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of DataStorageOrder (ent.graphql:2176-2185).</summary>
[GraphQLName("DataStorageOrder")]
[InputObjectType("DataStorageOrder")]
public sealed class DataStorageOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    /// <summary>Non-null in the SDL: the field must be provided when orderBy is.</summary>
    public DataStorageOrderFieldValue Field { get; set; }
}

/// <summary>Port of <c>DataStorageWhereInput</c> (axonhub.graphql).</summary>
[GraphQLName("DataStorageWhereInput")]
public sealed class DataStorageWhereInput
{
    public DataStorageWhereInput? Not { get; set; }

    public IReadOnlyList<DataStorageWhereInput>? And { get; set; }

    public IReadOnlyList<DataStorageWhereInput>? Or { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdNotIn { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdGT { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdGTE { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdLT { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdLTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? CreatedAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? CreatedAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtLTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? UpdatedAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? UpdatedAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtLTE { get; set; }

    public string? Name { get; set; }

    public string? NameNEQ { get; set; }

    public IReadOnlyList<string>? NameIn { get; set; }

    public IReadOnlyList<string>? NameNotIn { get; set; }

    public string? NameGT { get; set; }

    public string? NameGTE { get; set; }

    public string? NameLT { get; set; }

    public string? NameLTE { get; set; }

    public string? NameContains { get; set; }

    public string? NameHasPrefix { get; set; }

    public string? NameHasSuffix { get; set; }

    public string? NameEqualFold { get; set; }

    public string? NameContainsFold { get; set; }

    public string? Description { get; set; }

    public string? DescriptionNEQ { get; set; }

    public IReadOnlyList<string>? DescriptionIn { get; set; }

    public IReadOnlyList<string>? DescriptionNotIn { get; set; }

    public string? DescriptionGT { get; set; }

    public string? DescriptionGTE { get; set; }

    public string? DescriptionLT { get; set; }

    public string? DescriptionLTE { get; set; }

    public string? DescriptionContains { get; set; }

    public string? DescriptionHasPrefix { get; set; }

    public string? DescriptionHasSuffix { get; set; }

    public string? DescriptionEqualFold { get; set; }

    public string? DescriptionContainsFold { get; set; }

    /// <summary>Nullable in the SDL; an explicit false is a predicate too.</summary>
    public bool? Primary { get; set; }

    public bool? PrimaryNEQ { get; set; }

    public DataStorageTypeValue? Type { get; set; }

    public DataStorageTypeValue? TypeNEQ { get; set; }

    public IReadOnlyList<DataStorageTypeValue>? TypeIn { get; set; }

    public IReadOnlyList<DataStorageTypeValue>? TypeNotIn { get; set; }

    public DataStorageStatusValue? Status { get; set; }

    public DataStorageStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<DataStorageStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<DataStorageStatusValue>? StatusNotIn { get; set; }

    public bool? HasRequests { get; set; }

    public IReadOnlyList<RequestWhereInput>? HasRequestsWith { get; set; }

    public bool? HasExecutions { get; set; }

    public IReadOnlyList<RequestExecutionWhereInput>? HasExecutionsWith { get; set; }
}

/// <summary>Port of DataStorageSettingsInput (axonhub.graphql:696-703).</summary>
[GraphQLName("DataStorageSettingsInput")]
public sealed class DataStorageSettingsInput
{
    public string? Dsn { get; set; }

    public string? Directory { get; set; }

    [GraphQLName("s3")]
    public S3Input? S3 { get; set; }

    [GraphQLName("gcs")]
    public GcsInput? Gcs { get; set; }

    [GraphQLName("webdav")]
    public WebDavInput? WebDav { get; set; }
}

/// <summary>Port of S3Input (axonhub.graphql:714-721).</summary>
[GraphQLName("S3Input")]
public sealed class S3Input
{
    public string BucketName { get; set; } = string.Empty;

    public string? Endpoint { get; set; }

    public string? Region { get; set; }

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    public bool? PathStyle { get; set; }
}

/// <summary>Port of GCSInput (axonhub.graphql:723-726).</summary>
[GraphQLName("GCSInput")]
public sealed class GcsInput
{
    public string BucketName { get; set; } = string.Empty;

    public string? Credential { get; set; }
}

/// <summary>Port of WebDAVInput (axonhub.graphql:705-712).</summary>
[GraphQLName("WebDAVInput")]
public sealed class WebDavInput
{
    public string Url { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? Password { get; set; }

    [GraphQLName("insecure_skip_tls")]
    public bool? InsecureSkipTls { get; set; }

    public string? Path { get; set; }
}

/// <summary>Port of the ent <c>CreateDataStorageInput</c> (ent.graphql:1671-1692).</summary>
[GraphQLName("CreateDataStorageInput")]
public sealed class CreateDataStorageInput
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public DataStorageTypeValue Type { get; set; }

    public DataStorageSettingsInput Settings { get; set; } = new();

    /// <summary>Accepted but ignored by the reference's create.</summary>
    public DataStorageStatusValue? Status { get; set; }
}

/// <summary>Port of the ent <c>UpdateDataStorageInput</c> (ent.graphql:6260-6277).</summary>
[GraphQLName("UpdateDataStorageInput")]
public sealed class UpdateDataStorageInput
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public DataStorageSettingsInput? Settings { get; set; }

    public DataStorageStatusValue? Status { get; set; }
}

/// <summary>Port of the <c>DataStorageSettings</c> output type (axonhub.graphql:726-732).</summary>
[GraphQLName("DataStorageSettings")]
public sealed class DataStorageSettingsType
{
    [GraphQLIgnore]
    public DataStorageSettings Settings { get; init; } = new();

    public string? Dsn => Settings.Dsn;

    public string? Directory => Settings.Directory;

    [GraphQLName("s3")]
    public S3Type? S3 => Settings.S3 is { } s3
        ? new S3Type
        {
            BucketName = s3.BucketName,
            Endpoint = s3.Endpoint,
            Region = s3.Region,
            PathStyle = s3.PathStyle,
        }
        : null;

    [GraphQLName("gcs")]
    public GcsType? Gcs => Settings.Gcs is { } gcs
        ? new GcsType { BucketName = gcs.BucketName }
        : null;

    [GraphQLName("webdav")]
    public WebDavType? WebDav => Settings.WebDav is { } webDav
        ? new WebDavType
        {
            Url = webDav.Url,
            Username = webDav.Username,
            Password = webDav.Password,
            InsecureSkipTls = webDav.InsecureSkipTls,
            Path = webDav.Path,
        }
        : null;
}

/// <summary>Port of the <c>S3</c> output type (axonhub.graphql:742-747).</summary>
[GraphQLName("S3")]
public sealed class S3Type
{
    public string BucketName { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public bool PathStyle { get; init; }
}

/// <summary>Port of the <c>GCS</c> output type (axonhub.graphql:749-751).</summary>
[GraphQLName("GCS")]
public sealed class GcsType
{
    public string BucketName { get; init; } = string.Empty;
}

/// <summary>Port of the <c>WebDAV</c> output type (axonhub.graphql:734-740).</summary>
/// <remarks>
/// The SDL marks username/password/insecure_skip_tls/path nullable, but Go's
/// fields are plain strings and bools, so the reference never emits null;
/// the port keeps the nullable schema shape and always assigns a value.
/// </remarks>
[GraphQLName("WebDAV")]
public sealed class WebDavType
{
    public string Url { get; init; } = string.Empty;

    public string? Username { get; init; }

    public string? Password { get; init; }

    [GraphQLName("insecure_skip_tls")]
    public bool? InsecureSkipTls { get; init; }

    public string? Path { get; init; }
}

/// <summary>Port of the <c>DataStorage</c> object type (ent.graphql:2052-2110).</summary>
/// <remarks>
/// The reference also exposes <c>requests</c> and <c>executions</c> relay
/// connections; they are not part of this slice (C196).
/// </remarks>
[GraphQLName("DataStorage")]
public sealed class DataStorageType : INode
{
    [GraphQLIgnore]
    public DataStorageRecord Record { get; init; } = null!;

    [GraphQLIgnore]
    public long DbId => Record.Id;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("DataStorage", Record.Id).ToString();

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt => Record.CreatedAt;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt => Record.UpdatedAt;

    public string Name => Record.Name;

    public string Description => Record.Description;

    public bool Primary => Record.Primary;

    public DataStorageTypeValue Type => DataStorageMapping.ToGraphQLType(Record.Type);

    /// <summary>
    /// ent scans the JSON column into *objects.DataStorageSettings; a NULL
    /// column (or a JSON null) leaves the pointer nil and gqlgen fails the
    /// non-null field with its "requested element is null" message.
    /// </summary>
    public DataStorageSettingsType Settings
        => DataStorageSettingsCodec.Deserialize(Record.Settings) is { } settings
            ? new DataStorageSettingsType { Settings = settings }
            : throw new GraphQLException(
                "the requested element is null which the schema does not allow");

    public DataStorageStatusValue Status => DataStorageMapping.ToGraphQLStatus(Record.Status);
}

/// <summary>Port of <c>DataStorageEdge</c> (ent.graphql:2154-2163).</summary>
[GraphQLName("DataStorageEdge")]
public sealed class DataStorageEdgeType
{
    public DataStorageType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>DataStorageConnection</c> (ent.graphql:2135-2152).</summary>
[GraphQLName("DataStorageConnection")]
public sealed class DataStorageConnectionType
{
    public IReadOnlyList<DataStorageEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Domain ↔ GraphQL mapping for the data storages slice.</summary>
public static class DataStorageMapping
{
    public static DataStorageType ToGraphQL(DataStorageRecord record) => new() { Record = record };

    public static DataStorageListFilter? ToFilter(DataStorageWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new DataStorageListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdNEQ = where.IdNEQ is null ? null : GidValue.Parse(where.IdNEQ).Id,
            IdIn = where.IdIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            IdNotIn = where.IdNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            IdGT = where.IdGT is null ? null : GidValue.Parse(where.IdGT).Id,
            IdGTE = where.IdGTE is null ? null : GidValue.Parse(where.IdGTE).Id,
            IdLT = where.IdLT is null ? null : GidValue.Parse(where.IdLT).Id,
            IdLTE = where.IdLTE is null ? null : GidValue.Parse(where.IdLTE).Id,
            CreatedAt = where.CreatedAt,
            CreatedAtNEQ = where.CreatedAtNEQ,
            CreatedAtIn = where.CreatedAtIn,
            CreatedAtNotIn = where.CreatedAtNotIn,
            CreatedAtGT = where.CreatedAtGT,
            CreatedAtGTE = where.CreatedAtGTE,
            CreatedAtLT = where.CreatedAtLT,
            CreatedAtLTE = where.CreatedAtLTE,
            UpdatedAt = where.UpdatedAt,
            UpdatedAtNEQ = where.UpdatedAtNEQ,
            UpdatedAtIn = where.UpdatedAtIn,
            UpdatedAtNotIn = where.UpdatedAtNotIn,
            UpdatedAtGT = where.UpdatedAtGT,
            UpdatedAtGTE = where.UpdatedAtGTE,
            UpdatedAtLT = where.UpdatedAtLT,
            UpdatedAtLTE = where.UpdatedAtLTE,
            Name = where.Name,
            NameNEQ = where.NameNEQ,
            NameIn = where.NameIn,
            NameNotIn = where.NameNotIn,
            NameGT = where.NameGT,
            NameGTE = where.NameGTE,
            NameLT = where.NameLT,
            NameLTE = where.NameLTE,
            NameContains = where.NameContains,
            NameHasPrefix = where.NameHasPrefix,
            NameHasSuffix = where.NameHasSuffix,
            NameEqualFold = where.NameEqualFold,
            NameContainsFold = where.NameContainsFold,
            Description = where.Description,
            DescriptionNEQ = where.DescriptionNEQ,
            DescriptionIn = where.DescriptionIn,
            DescriptionNotIn = where.DescriptionNotIn,
            DescriptionGT = where.DescriptionGT,
            DescriptionGTE = where.DescriptionGTE,
            DescriptionLT = where.DescriptionLT,
            DescriptionLTE = where.DescriptionLTE,
            DescriptionContains = where.DescriptionContains,
            DescriptionHasPrefix = where.DescriptionHasPrefix,
            DescriptionHasSuffix = where.DescriptionHasSuffix,
            DescriptionEqualFold = where.DescriptionEqualFold,
            DescriptionContainsFold = where.DescriptionContainsFold,
            // entgql binds these to *bool, so an explicit false is a predicate.
            Primary = where.Primary,
            PrimaryNEQ = where.PrimaryNEQ,
            Type = where.Type is null ? null : ToWireType(where.Type.Value),
            TypeNEQ = where.TypeNEQ is null ? null : ToWireType(where.TypeNEQ.Value),
            TypeIn = where.TypeIn?
                .Select(static type => ToWireType(type))
                .ToList(),
            TypeNotIn = where.TypeNotIn?
                .Select(static type => ToWireType(type))
                .ToList(),
            Status = where.Status is null ? null : ToWireStatus(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null ? null : ToWireStatus(where.StatusNEQ.Value),
            StatusIn = where.StatusIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            StatusNotIn = where.StatusNotIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            HasRequests = where.HasRequests,
            HasRequestsWith = where.HasRequestsWith?
                .Select(static nested => TraceMapping.ToFilter(nested))
                .OfType<RequestListFilter>()
                .ToList(),
            HasExecutions = where.HasExecutions,
            HasExecutionsWith = where.HasExecutionsWith?
                .Select(static nested => RequestMapping.ToFilter(nested))
                .OfType<RequestExecutionListFilter>()
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<DataStorageListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<DataStorageListFilter>().ToList(),
        };
    }

    /// <summary>
    /// The resolver rewrites a CREATED_AT order to ent.DefaultDataStorageOrder's
    /// field, which is <c>id</c> (ent/gql_pagination.go); UPDATED_AT orders by
    /// the updated_at column. The direction is preserved either way.
    /// </summary>
    public static DataStorageSortField ToSortField(DataStorageOrder? orderBy)
        => orderBy?.Field switch
        {
            DataStorageOrderFieldValue.UpdatedAt => DataStorageSortField.UpdatedAt,
            _ => DataStorageSortField.Id,
        };

    public static SortDirection ToSortDirection(DataStorageOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static DataStorageConnectionType BuildConnection(
        ListPage<DataStorageRecord> page,
        DataStorageSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new DataStorageEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField switch
                    {
                        DataStorageSortField.UpdatedAt => node.UpdatedAt,
                        _ => null,
                    }),
            })
            .ToList();

        return new DataStorageConnectionType
        {
            Edges = edges,
            PageInfo = new PageInfoType
            {
                HasNextPage = page.HasNextPage,
                HasPreviousPage = page.HasPreviousPage,
                StartCursor = edges.Count > 0 ? edges[0].Cursor : null,
                EndCursor = edges.Count > 0 ? edges[^1].Cursor : null,
            },
            TotalCount = page.TotalCount,
        };
    }

    /// <summary>
    /// Port of gqlgen's DataStorageSettingsInput → objects.DataStorageSettings
    /// binding: absent nested objects decode as nil, absent scalars as "" or
    /// false.
    /// </summary>
    public static DataStorageSettings ToDomain(DataStorageSettingsInput input) => new()
    {
        Dsn = input.Dsn,
        Directory = input.Directory,
        S3 = input.S3 is null
            ? null
            : new S3StorageSettings
            {
                BucketName = input.S3.BucketName,
                Endpoint = input.S3.Endpoint ?? string.Empty,
                Region = input.S3.Region ?? string.Empty,
                AccessKey = input.S3.AccessKey ?? string.Empty,
                SecretKey = input.S3.SecretKey ?? string.Empty,
                PathStyle = input.S3.PathStyle ?? false,
            },
        Gcs = input.Gcs is null
            ? null
            : new GcsStorageSettings
            {
                BucketName = input.Gcs.BucketName,
                Credential = input.Gcs.Credential ?? string.Empty,
            },
        WebDav = input.WebDav is null
            ? null
            : new WebDavStorageSettings
            {
                Url = input.WebDav.Url,
                Username = input.WebDav.Username ?? string.Empty,
                Password = input.WebDav.Password ?? string.Empty,
                InsecureSkipTls = input.WebDav.InsecureSkipTls ?? false,
                Path = input.WebDav.Path ?? string.Empty,
            },
    };

    public static CreateDataStorageCommand ToCommand(CreateDataStorageInput input) => new()
    {
        Name = input.Name,
        Description = input.Description,
        Type = ToWireType(input.Type),
        Settings = ToDomain(input.Settings),
        Status = input.Status is null ? null : ToWireStatus(input.Status.Value),
    };

    public static UpdateDataStorageCommand ToCommand(string id, UpdateDataStorageInput input) => new()
    {
        Id = GidValue.Parse(id).Id,
        Name = input.Name,
        Description = input.Description,
        Settings = input.Settings is null ? null : ToDomain(input.Settings),
        Status = input.Status is null ? null : ToWireStatus(input.Status.Value),
    };

    public static string ToWireType(DataStorageTypeValue type) => type switch
    {
        DataStorageTypeValue.Database => "database",
        DataStorageTypeValue.Fs => "fs",
        DataStorageTypeValue.S3 => "s3",
        DataStorageTypeValue.Gcs => "gcs",
        DataStorageTypeValue.WebDav => "webdav",
        _ => throw new GraphQLException(
            "Enum \"DataStorageType\" cannot represent value: \"" + type + "\""),
    };

    public static DataStorageTypeValue ToGraphQLType(string type) => type switch
    {
        "database" => DataStorageTypeValue.Database,
        "fs" => DataStorageTypeValue.Fs,
        "s3" => DataStorageTypeValue.S3,
        "gcs" => DataStorageTypeValue.Gcs,
        "webdav" => DataStorageTypeValue.WebDav,
        _ => throw new GraphQLException(
            "Enum \"DataStorageType\" cannot represent value: \"" + type + "\""),
    };

    public static string ToWireStatus(DataStorageStatusValue status) => status switch
    {
        DataStorageStatusValue.Active => "active",
        DataStorageStatusValue.Archived => "archived",
        _ => throw new GraphQLException(
            "Enum \"DataStorageStatus\" cannot represent value: \"" + status + "\""),
    };

    public static DataStorageStatusValue ToGraphQLStatus(string status) => status switch
    {
        "active" => DataStorageStatusValue.Active,
        "archived" => DataStorageStatusValue.Archived,
        _ => throw new GraphQLException(
            "Enum \"DataStorageStatus\" cannot represent value: \"" + status + "\""),
    };
}
