using System.Text;
using Microsoft.Extensions.Configuration;
using YamlDotNet.RepresentationModel;

namespace AxonHub.Api.Configuration;

/// <summary>
/// Minimal YAML configuration source for the reference's <c>config.yml</c>.
/// Keys are flattened to ASP.NET Core's <c>Section:Key</c> form and each
/// snake_case segment is converted to PascalCase, so the port's existing
/// configuration readers (which use keys such as <c>Server:RequestTimeout</c>)
/// work unchanged. Environment variables and appsettings are added after this
/// source and therefore keep precedence over the file.
/// </summary>
public sealed class YamlConfigurationSource : IConfigurationSource
{
    public required string Path { get; init; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new YamlConfigurationProvider(Path);
}

/// <summary>Loads the file on every reload (SIGHUP calls IConfiguration.Reload).</summary>
public sealed class YamlConfigurationProvider(string path) : ConfigurationProvider
{
    public override void Load()
    {
        var values = File.Exists(path)
            ? YamlConfigurationParser.Parse(File.ReadAllText(path))
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        Data.Clear();
        foreach (var (key, value) in values)
        {
            Data[key] = value;
        }
    }
}

/// <summary>Finds the reference config.yml in the app or working directory.</summary>
public static class YamlConfigurationFiles
{
    public static string? Find(string contentRootPath, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(contentRootPath);
        ArgumentNullException.ThrowIfNull(currentDirectory);

        var candidates = new[]
        {
            Path.Combine(contentRootPath, "config.yml"),
            Path.Combine(contentRootPath, "config.yaml"),
            Path.Combine(currentDirectory, "config.yml"),
            Path.Combine(currentDirectory, "config.yaml"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}

/// <summary>Flattens YAML mappings/sequences into IConfiguration key/value pairs.</summary>
public static class YamlConfigurationParser
{
    public static Dictionary<string, string?> Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (stream.Documents.Count == 0)
        {
            return values;
        }

        Flatten(stream.Documents[0].RootNode, string.Empty, values);
        return values;
    }

    private static void Flatten(
        YamlNode node,
        string path,
        Dictionary<string, string?> values)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var entry in mapping.Children)
                {
                    if (entry.Key is not YamlScalarNode key || string.IsNullOrEmpty(key.Value))
                    {
                        continue;
                    }

                    var childPath = path.Length == 0
                        ? ToPascal(key.Value)
                        : path + ":" + ToPascal(key.Value);
                    Flatten(entry.Value, childPath, values);
                }

                break;

            case YamlSequenceNode sequence:
                if (sequence.Children.Count == 0)
                {
                    // An explicit empty list. List readers check for children
                    // first, then fall back to this empty scalar.
                    values[path] = string.Empty;
                    break;
                }

                var scalars = new List<string>(sequence.Children.Count);
                var allScalars = true;
                for (var index = 0; index < sequence.Children.Count; index++)
                {
                    var child = sequence.Children[index];
                    if (child is YamlScalarNode scalar)
                    {
                        scalars.Add(scalar.Value ?? string.Empty);
                    }
                    else
                    {
                        allScalars = false;
                    }

                    Flatten(child, path + ":" + index, values);
                }

                if (allScalars)
                {
                    // Some readers consume the list as a comma-separated scalar
                    // instead of binding the indexed children.
                    values[path] = string.Join(",", scalars);
                }

                break;

            case YamlScalarNode scalar:
                values[path] = scalar.Value ?? string.Empty;
                break;
        }
    }

    private static string ToPascal(string value)
    {
        var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        foreach (var part in parts)
        {
            result.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                result.Append(part.AsSpan(1));
            }
        }

        return result.ToString();
    }
}
