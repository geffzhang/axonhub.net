using AxonHub.Api.Orchestration;

namespace AxonHub.Api.PromptProtection;

/// <summary>
/// The gjson reads prompt protection performs on raw bodies. Only simple paths
/// (no wildcards) are used by the reference, which is exactly the subset
/// <see cref="OverrideJson"/> implements.
/// </summary>
internal static class PromptProtectionJson
{
    /// <summary>Port of <c>int(gjson.GetBytes(body, path+".#").Int())</c>.</summary>
    public static int ArrayLength(byte[] body, string path)
    {
        var lookup = OverrideJson.Get(body, path);
        switch (lookup.Node)
        {
            case OverrideJsonArray array:
                return array.Items.Count;

            // gjson treats '#' as an ordinary key inside an object.
            case OverrideJsonObject obj:
                var member = obj.FindMember("#");
                return member is null ? 0 : unchecked((int)new JsonLookup(member.Value).IntValue);

            default:
                return 0;
        }
    }

    /// <summary>gjson.GetBytes(...).String().</summary>
    public static string String(byte[] body, string path)
        => OverrideJson.Get(body, path).StringValue;

    /// <summary>gjson.GetBytes(...).Type == gjson.Null.</summary>
    public static bool IsNull(byte[] body, string path)
        => OverrideJson.Get(body, path).IsNull;

    /// <summary>gjson.GetBytes(...).Type == gjson.String.</summary>
    public static bool IsString(byte[] body, string path)
        => OverrideJson.Get(body, path).IsString;

    /// <summary>gjson.GetBytes(...).Type == gjson.JSON.</summary>
    public static bool IsJson(byte[] body, string path)
        => OverrideJson.Get(body, path).IsJson;

    /// <summary>gjson.GetBytes(...).Exists().</summary>
    public static bool Exists(byte[] body, string path)
        => OverrideJson.Get(body, path).Exists;
}
