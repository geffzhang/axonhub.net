using System.Text;
using System.Text.RegularExpressions;
using AxonHub.Api.Catalog;
using AxonHub.Api.Settings;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports <c>internal/server/biz/catalog_filter_test.go</c>: the developer
/// allow-list, the meta/llama and per-plan merges, deterministic catalog
/// ordering, the catalog-settings URL validation texts and the frontend
/// developer-id drift check.
/// </summary>
public sealed class CatalogFilterTests
{
    [Fact]
    public void FilterCatalogProvidersKeepsTheAllowListAndMergesTheSpecialProviders()
    {
        var input = Parse(
            """
            {"providers":{
              "openai":{"id":"openai","models":[{"id":"gpt-5.6-luna","release_date":"2026-03-01"}]},
              "unknown-lab":{"id":"unknown-lab","models":[{"id":"secret-model"}]},
              "llama":{"id":"llama","models":[{"id":"llama-4"},{"id":"other-7b"}]},
              "nvidia":{"id":"nvidia","models":[{"id":"nvidia/nemotron"},{"id":"meta/llama-hosted"}]},
              "some-host":{"id":"some-host","models":[{"id":"kwaipilot/kat-coder-pro","family":"kat-coder"}]},
              "thinkingmachines":{"id":"thinkingmachines","models":[{"id":"tinker-only"}]}
            }}
            """);

        var filtered = CatalogFilter.FilterProviders(input, CatalogFilter.DefaultDeveloperIds);
        var providers = filtered.Providers!;

        Assert.True(providers.ContainsKey("openai"));
        Assert.False(providers.ContainsKey("unknown-lab"));
        Assert.Equal("meta", providers["meta"].Id);
        Assert.Equal(["llama-4"], ModelIds(providers["meta"]));
        Assert.Equal(["nvidia/nemotron"], ModelIds(providers["nvidia"]));
        Assert.Equal(["kat-coder-pro"], ModelIds(providers["kwaipilot"]));
        Assert.False(providers.ContainsKey("thinkingmachines"));

        var extra = Encoding.UTF8.GetBytes(
            """{"thinkingmachines":[{"id":"tm-canonical","release_date":"2026-01-01"}]}""");
        CatalogFilter.MergeExtraModels(filtered, extra);
        Assert.Equal(["tm-canonical"], ModelIds(providers["thinkingmachines"]));
    }

    [Fact]
    public void FilterCatalogProvidersMergesUpstreamMetaAndLlama()
    {
        var input = Parse(
            """
            {"providers":{
              "meta":{"id":"meta","models":[
                {"id":"muse-1","source":"meta"},
                {"id":"llama-4","source":"meta"}
              ]},
              "llama":{"id":"llama","models":[
                {"id":"llama-4","source":"llama"},
                {"id":"llama-5","source":"llama"},
                {"id":"other-7b"}
              ]}
            }}
            """);

        var filtered = CatalogFilter.FilterProviders(input, CatalogFilter.DefaultDeveloperIds);
        var meta = filtered.Providers!["meta"];
        var models = meta.Models!;

        Assert.Equal(["llama-4", "llama-5", "muse-1"], ModelIds(meta));
        Assert.Equal("meta", CatalogFilter.ModelString(models[0], "source"));
        Assert.Equal("llama", CatalogFilter.ModelString(models[1], "source"));
    }

    [Fact]
    public void ValidateCatalogSettingsMatchesTheReferenceAcceptRejectSplit()
    {
        // catalog_filter_test.go:100-107.
        Assert.Null(CatalogSettingsRules.Validate(new CatalogSettings("", 0)));
        Assert.NotNull(CatalogSettingsRules.Validate(new CatalogSettings("ftp://x", 0)));
        Assert.NotNull(CatalogSettingsRules.Validate(new CatalogSettings("not-a-url", 0)));
        Assert.Null(CatalogSettingsRules.Validate(
            new CatalogSettings(CatalogSettingsRules.DefaultUpstreamUrl, 0)));
    }

    [Theory]
    [InlineData("https://example.com", null)]
    [InlineData("HTTP://Example.COM:80/x", null)]
    [InlineData("https://:8080/x", null)]
    [InlineData("https://example.com:/x", null)]
    [InlineData("https://example.com/x?q=%zz", null)]
    [InlineData("https://example.com/x#fr\tag", null)]
    [InlineData("https://exa%80mple.com/x", null)]
    [InlineData("https://[fe80::1%25eth0]/x", null)]
    [InlineData("*", "catalog upstream url must be http or https")]
    [InlineData("https:example.com", "catalog upstream url is missing a host")]
    [InlineData("https:///x", "catalog upstream url is missing a host")]
    [InlineData("https://user@/x", "catalog upstream url is missing a host")]
    [InlineData(
        "https://example.com:80:90/x",
        """invalid catalog upstream url: parse "https://example.com:80:90/x": invalid port ":80:90" after host""")]
    [InlineData(
        "https://example.com:&x",
        """invalid catalog upstream url: parse "https://example.com:&x": invalid port ":&x" after host""")]
    [InlineData(
        "https://[1.2.3.4]/x",
        """invalid catalog upstream url: parse "https://[1.2.3.4]/x": invalid IP-literal""")]
    [InlineData(
        "https://[abc]/x",
        """invalid catalog upstream url: parse "https://[abc]/x": invalid host: ParseAddr("abc"): unable to parse IP""")]
    [InlineData(
        "https://[]/x",
        """invalid catalog upstream url: parse "https://[]/x": invalid host: ParseAddr(""): unable to parse IP""")]
    [InlineData(
        "https://[fe80::1%25]/x",
        """invalid catalog upstream url: parse "https://[fe80::1%25]/x": invalid host: ParseAddr("fe80::1%"): zone must be a non-empty string""")]
    [InlineData(
        "https://[::1%eth0]/x",
        "invalid catalog upstream url: parse \"https://[::1%eth0]/x\": invalid URL escape \"%et\"")]
    [InlineData(
        "https://exa%41mple.com/x",
        "invalid catalog upstream url: parse \"https://exa%41mple.com/x\": invalid URL escape \"%41\"")]
    [InlineData(
        "https://exa mple.com/#frag",
        """invalid catalog upstream url: parse "https://exa mple.com/": invalid character " " in host name""")]
    [InlineData(
        "https://user:pa ss@example.com/x",
        """invalid catalog upstream url: parse "https://user:pa ss@example.com/x": net/url: invalid userinfo""")]
    [InlineData(
        "https://user%zz@example.com/x",
        "invalid catalog upstream url: parse \"https://user%zz@example.com/x\": invalid URL escape \"%zz\"")]
    [InlineData(
        "https://example.com/x#frag%zz",
        "invalid catalog upstream url: parse \"https://example.com/x#frag%zz\": invalid URL escape \"%zz\"")]
    [InlineData(
        ":foo",
        """invalid catalog upstream url: parse ":foo": missing protocol scheme""")]
    [InlineData(
        "https:/x%zz",
        "invalid catalog upstream url: parse \"https:/x%zz\": invalid URL escape \"%zz\"")]
    [InlineData(
        "  https://example.com/x  ",
        """invalid catalog upstream url: parse "  https://example.com/x  ": first path segment in URL cannot contain colon""")]
    [InlineData(
        "https://example.com/a\tb",
        "invalid catalog upstream url: parse \"https://example.com/a\\tb\": net/url: invalid control character in URL")]
    public void ValidateCatalogSettingsMirrorsGoUrlParse(string upstreamUrl, string? expected)
    {
        Assert.Equal(expected, CatalogSettingsRules.Validate(new CatalogSettings(upstreamUrl, 0)));
    }

    [Fact]
    public void CatalogFallbackJsonParses()
    {
        var assembly = typeof(CatalogSettingsRules).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "AxonHub.Api.Catalog.Data.providers.json");
        Assert.NotNull(stream);

        using var memory = new MemoryStream();
        stream!.CopyTo(memory);
        var data = CatalogParser.ParseFile(memory.ToArray());

        Assert.NotEmpty(data.Providers!);
    }

    [Fact]
    public void CatalogFallbackExtraModelsJsonParsesAndMerges()
    {
        var assembly = typeof(CatalogSettingsRules).Assembly;
        using var providersStream = assembly.GetManifestResourceStream(
            "AxonHub.Api.Catalog.Data.providers.json");
        using var modelsStream = assembly.GetManifestResourceStream(
            "AxonHub.Api.Catalog.Data.models.json");
        Assert.NotNull(providersStream);
        Assert.NotNull(modelsStream);

        using var providersMemory = new MemoryStream();
        providersStream!.CopyTo(providersMemory);
        using var modelsMemory = new MemoryStream();
        modelsStream!.CopyTo(modelsMemory);

        var data = CatalogParser.ParseFile(providersMemory.ToArray());
        CatalogFilter.MergeExtraModels(data, modelsMemory.ToArray());
        CatalogFilter.SortCatalogModels(data);

        Assert.NotEmpty(data.Providers!);
    }

    [Fact]
    public void FilterCatalogProvidersProducesDeterministicOrder()
    {
        var input = Parse(
            """
            {"providers":{
              "tencent":{"id":"tencent","models":[
                {"id":"hunyuan-b","release_date":"2026-01-01"},
                {"id":"hunyuan-a","release_date":"2026-01-01"}
              ]},
              "tencent-token-plan":{"id":"tencent-token-plan","models":[
                {"id":"hunyuan-c","release_date":"2026-01-01"}
              ]},
              "xiaomi":{"id":"xiaomi","models":[
                {"id":"mimo-b","release_date":"2026-02-01"},
                {"id":"mimo-a","release_date":"2026-02-01"}
              ]},
              "host-b":{"id":"host-b","models":[
                {"id":"kwaipilot/kat-coder-pro","family":"kat-coder","release_date":"2026-01-01"}
              ]},
              "host-a":{"id":"host-a","models":[
                {"id":"kuaishou/kat-dev","family":"kat-coder","release_date":"2026-01-01"}
              ]},
              "ibm-b":{"id":"ibm-b","models":[
                {"id":"granite-y","release_date":"2025-01-01"}
              ]},
              "ibm-a":{"id":"ibm-a","models":[
                {"id":"granite-x","release_date":"2025-01-01"}
              ]}
            }}
            """);

        string? encoded = null;
        CatalogFile filtered = null!;
        for (var iteration = 0; iteration < 20; iteration++)
        {
            filtered = CatalogFilter.FilterProviders(input, CatalogFilter.DefaultDeveloperIds);
            CatalogFilter.SortCatalogModels(filtered);
            var raw = Encoding.UTF8.GetString(filtered.ToUtf8Bytes());
            if (iteration == 0)
            {
                encoded = raw;
                continue;
            }

            Assert.Equal(encoded, raw);
        }

        var providers = filtered.Providers!;
        Assert.Equal(["hunyuan-a", "hunyuan-b", "hunyuan-c"], ModelIds(providers["tencent"]));
        Assert.Equal(["mimo-a", "mimo-b"], ModelIds(providers["xiaomi"]));
        Assert.Equal(["kat-coder-pro", "kat-dev"], ModelIds(providers["kwaipilot"]));
        Assert.Equal(["granite-x", "granite-y"], ModelIds(providers["ibm"]));
    }

    [Fact]
    public void SortModelsByReleaseDateUsesIdAsTieBreaker()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """
            [
              {"id":"b","release_date":"2026-01-01"},
              {"id":"a","release_date":"2026-01-01"},
              {"id":"c","release_date":"2026-02-01"}
            ]
            """);
        var models = CatalogParser.ParseModels(document.RootElement);

        var sorted = CatalogFilter.SortModelsByReleaseDate(models);

        Assert.Equal(
            ["c", "a", "b"],
            sorted.Select(model => CatalogFilter.ModelString(model, "id")));
    }

    [Fact]
    public void DefaultDeveloperIdsMatchFrontendConstants()
    {
        var constantsPath = FindFrontendConstants();
        var content = File.ReadAllText(constantsPath);

        var match = Regex.Match(content, @"export const DEVELOPER_IDS = \[([\s\S]*?)\]");
        Assert.True(match.Success);

        var frontend = Regex.Matches(match.Groups[1].Value, @"['""]([^'""]+)['""]")
            .Select(static item => item.Groups[1].Value)
            .ToArray();

        Assert.Equal(CatalogFilter.DefaultDeveloperIds, frontend);
    }

    private static CatalogFile Parse(string json)
        => CatalogParser.ParseFile(Encoding.UTF8.GetBytes(json));

    private static string[] ModelIds(CatalogProvider provider)
        => [.. (provider.Models ?? []).Select(model => CatalogFilter.ModelString(model, "id"))];

    private static string FindFrontendConstants()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "frontend",
                "src",
                "features",
                "models",
                "data",
                "constants.ts");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repository root not found");
    }
}
