using System.Text.Json;
using System.Text.Json.Serialization;

namespace Toolbox.PluginHost;

/// <summary>
/// 可选的插件清单 <c>plugin.json</c>（与入口程序集同目录）。
/// </summary>
/// <remarks>
/// 建议每个插件都带一份：
/// <list type="bullet">
///   <item>zip 包里只要不止一个 dll，就必须靠 <see cref="EntryAssembly"/> 指定入口；</item>
///   <item><see cref="SharedAssemblies"/> 让插件可以声明"我和宿主共享这个程序集"；</item>
///   <item><see cref="Settings"/> 是插件默认配置，会被主程序配置同键覆盖。</item>
/// </list>
/// </remarks>
public sealed class PluginManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    /// <summary>入口程序集文件名（相对插件根目录），例如 <c>Acme.Toolbox.Clock.dll</c>。</summary>
    [JsonPropertyName("entryAssembly")]
    public string? EntryAssembly { get; set; }

    /// <summary>需要由默认 ALC 提供的程序集简单名，会与宿主全局清单合并。</summary>
    [JsonPropertyName("sharedAssemblies")]
    public string[]? SharedAssemblies { get; set; }

    /// <summary>插件默认配置（扁平键值）。</summary>
    [JsonPropertyName("settings")]
    public Dictionary<string, string?>? Settings { get; set; }

    public const string FileName = "plugin.json";

    /// <summary>读取 <paramref name="pluginRoot"/> 下的清单；不存在或解析失败返回 <see langword="null"/>。</summary>
    public static PluginManifest? TryLoad(string pluginRoot, Action<string>? onError = null)
    {
        var path = System.IO.Path.Combine(pluginRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            onError?.Invoke($"解析 {path} 失败：{ex.Message}");
            return null;
        }
    }
}
