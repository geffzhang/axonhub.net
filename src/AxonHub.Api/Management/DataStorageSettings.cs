using System.Text;
using System.Text.Json;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of objects.DataStorageSettings (internal/objects/data_stograge.go): the
/// JSON payload of <c>data_storages.settings</c>.
/// </summary>
/// <remarks>
/// Every field is nullable in Go (dsn/directory are <c>*string</c>, the three
/// backend blocks are pointers); absent pointers marshal as JSON <c>null</c>.
/// </remarks>
public sealed class DataStorageSettings
{
    /// <summary>Database DSN; sensitive, preserved on update while empty.</summary>
    public string? Dsn { get; init; }

    public string? Directory { get; init; }

    public S3StorageSettings? S3 { get; init; }

    public GcsStorageSettings? Gcs { get; init; }

    public WebDavStorageSettings? WebDav { get; init; }
}

/// <summary>Port of objects.S3.</summary>
public sealed class S3StorageSettings
{
    public string BucketName { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public string AccessKey { get; init; } = string.Empty;

    public string SecretKey { get; init; } = string.Empty;

    /// <summary>Path-style addressing for S3-compatible services (MinIO, Ceph).</summary>
    public bool PathStyle { get; init; }
}

/// <summary>Port of objects.GCS.</summary>
public sealed class GcsStorageSettings
{
    public string BucketName { get; init; } = string.Empty;

    public string Credential { get; init; } = string.Empty;
}

/// <summary>Port of objects.WebDAV.</summary>
public sealed class WebDavStorageSettings
{
    public string Url { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public bool InsecureSkipTls { get; init; }

    public string Path { get; init; } = string.Empty;
}

/// <summary>
/// Go-compatible marshal / unmarshal of objects.DataStorageSettings and the
/// settings merge performed by DataStorageService.mergeSettings
/// (internal/server/biz/data_storage.go:766-868).
/// </summary>
/// <remarks>
/// jsonb normalizes formatting and key order on write, so byte-for-byte
/// equality with the reference is not observable; semantic equality is. The
/// encoder still emits every field (Go has no <c>omitempty</c> here) so a
/// stored row read back by the reference decodes identically.
/// </remarks>
public static class DataStorageSettingsCodec
{
    /// <summary>Go marshals a nil settings pointer as the JSON literal <c>null</c>.</summary>
    public static string Serialize(DataStorageSettings? settings)
    {
        if (settings is null)
        {
            return "null";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            WriteNullableString(writer, "dsn", settings.Dsn);
            WriteNullableString(writer, "directory", settings.Directory);

            if (settings.S3 is { } s3)
            {
                writer.WritePropertyName("s3");
                writer.WriteStartObject();
                writer.WriteString("bucketName", s3.BucketName);
                writer.WriteString("endpoint", s3.Endpoint);
                writer.WriteString("region", s3.Region);
                writer.WriteString("accessKey", s3.AccessKey);
                writer.WriteString("secretKey", s3.SecretKey);
                writer.WriteBoolean("pathStyle", s3.PathStyle);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("s3");
            }

            if (settings.Gcs is { } gcs)
            {
                writer.WritePropertyName("gcs");
                writer.WriteStartObject();
                writer.WriteString("bucketName", gcs.BucketName);
                writer.WriteString("credential", gcs.Credential);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("gcs");
            }

            if (settings.WebDav is { } webDav)
            {
                writer.WritePropertyName("webdav");
                writer.WriteStartObject();
                writer.WriteString("url", webDav.Url);
                writer.WriteString("username", webDav.Username);
                writer.WriteString("password", webDav.Password);
                writer.WriteBoolean("insecure_skip_tls", webDav.InsecureSkipTls);
                writer.WriteString("path", webDav.Path);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("webdav");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Decodes the column the way ent's JSON field scan does: a SQL NULL column
    /// or the JSON literal <c>null</c> leaves the pointer nil. A malformed
    /// column fails the reference's row scan; the port degrades to no settings
    /// like the other port JSON readers (C141).
    /// </summary>
    public static DataStorageSettings? Deserialize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new DataStorageSettings
            {
                Dsn = ReadNullableString(root, "dsn"),
                Directory = ReadNullableString(root, "directory"),
                S3 = ReadObject(root, "s3") is { } s3
                    ? new S3StorageSettings
                    {
                        BucketName = ReadString(s3, "bucketName"),
                        Endpoint = ReadString(s3, "endpoint"),
                        Region = ReadString(s3, "region"),
                        AccessKey = ReadString(s3, "accessKey"),
                        SecretKey = ReadString(s3, "secretKey"),
                        PathStyle = ReadBoolean(s3, "pathStyle"),
                    }
                    : null,
                Gcs = ReadObject(root, "gcs") is { } gcs
                    ? new GcsStorageSettings
                    {
                        BucketName = ReadString(gcs, "bucketName"),
                        Credential = ReadString(gcs, "credential"),
                    }
                    : null,
                WebDav = ReadObject(root, "webdav") is { } webDav
                    ? new WebDavStorageSettings
                    {
                        Url = ReadString(webDav, "url"),
                        Username = ReadString(webDav, "username"),
                        Password = ReadString(webDav, "password"),
                        InsecureSkipTls = ReadBoolean(webDav, "insecure_skip_tls"),
                        Path = ReadString(webDav, "path"),
                    }
                    : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Port of DataStorageService.mergeSettings: sensitive fields that are
    /// empty in the input keep the stored value; a backend block is only
    /// treated as "provided" when at least one of its detection fields is
    /// non-empty (PathStyle and InsecureSkipTLS are deliberately excluded, so
    /// toggling only those is ignored); every other block is deep-copied from
    /// the existing settings.
    /// </summary>
    public static DataStorageSettings? Merge(
        DataStorageSettings? existing,
        DataStorageSettings? input)
    {
        // mergeSettings returns the existing settings unchanged for a nil input.
        if (input is null)
        {
            return existing;
        }

        var merged = new DataStorageSettings
        {
            // Directory (non-sensitive) and DSN (sensitive) win when provided.
            Directory = input.Directory ?? existing?.Directory,
            Dsn = input.Dsn ?? existing?.Dsn,
            S3 = MergeS3(existing?.S3, input.S3),
            Gcs = MergeGcs(existing?.Gcs, input.Gcs),
            WebDav = MergeWebDav(existing?.WebDav, input.WebDav),
        };

        return merged;
    }

    private static S3StorageSettings? MergeS3(
        S3StorageSettings? existing,
        S3StorageSettings? input)
    {
        if (IsS3Provided(input))
        {
            return new S3StorageSettings
            {
                BucketName = input!.BucketName,
                Endpoint = input.Endpoint,
                Region = input.Region,
                PathStyle = input.PathStyle,
                // Only update the sensitive fields when they are non-empty.
                AccessKey = input.AccessKey != string.Empty
                    ? input.AccessKey
                    : existing?.AccessKey ?? string.Empty,
                SecretKey = input.SecretKey != string.Empty
                    ? input.SecretKey
                    : existing?.SecretKey ?? string.Empty,
            };
        }

        return existing;
    }

    private static GcsStorageSettings? MergeGcs(
        GcsStorageSettings? existing,
        GcsStorageSettings? input)
    {
        if (IsGcsProvided(input))
        {
            return new GcsStorageSettings
            {
                BucketName = input!.BucketName,
                Credential = input.Credential != string.Empty
                    ? input.Credential
                    : existing?.Credential ?? string.Empty,
            };
        }

        return existing;
    }

    private static WebDavStorageSettings? MergeWebDav(
        WebDavStorageSettings? existing,
        WebDavStorageSettings? input)
    {
        if (IsWebDavProvided(input))
        {
            return new WebDavStorageSettings
            {
                Url = input!.Url,
                Username = input.Username,
                InsecureSkipTls = input.InsecureSkipTls,
                Path = input.Path,
                Password = input.Password != string.Empty
                    ? input.Password
                    : existing?.Password ?? string.Empty,
            };
        }

        return existing;
    }

    private static bool IsS3Provided(S3StorageSettings? s3)
        => s3 is not null
            && (s3.BucketName != string.Empty
                || s3.Endpoint != string.Empty
                || s3.Region != string.Empty
                || s3.AccessKey != string.Empty
                || s3.SecretKey != string.Empty);

    private static bool IsGcsProvided(GcsStorageSettings? gcs)
        => gcs is not null && (gcs.BucketName != string.Empty || gcs.Credential != string.Empty);

    private static bool IsWebDavProvided(WebDavStorageSettings? webDav)
        => webDav is not null
            && (webDav.Url != string.Empty
                || webDav.Username != string.Empty
                || webDav.Password != string.Empty
                || webDav.Path != string.Empty);

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static JsonElement? ReadObject(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? ReadNullableString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadString(JsonElement root, string name)
        => ReadNullableString(root, name) ?? string.Empty;

    private static bool ReadBoolean(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;
}
