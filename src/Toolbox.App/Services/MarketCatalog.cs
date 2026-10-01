using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Toolbox.App.Services;

/// <summary>市场里的一个可安装插件（来自 market/ 目录的 zip 或子目录）。</summary>
public sealed record MarketEntry(
    string Id,
    string DisplayName,
    string? Version,
    string Description,
    string SourcePath,
    string KindLabel);

/// <summary>
/// 插件市场目录（<c>{应用目录}/market/</c>）的扫描与种子打包。
/// </summary>
/// <remarks>
/// 市场是**纯本地**的：market/ 里放插件包（顶层 *.zip 或含 dll 的子目录），
/// 与 plugins/（已安装、会自动加载）完全分离 —— 市场里的东西不会被宿主加载。
/// 首次打开且 market/ 为空时，会把已安装的目录插件打包成 zip 并改写 manifest
/// （id 加 <c>-market</c> 后缀），保证任何部署形态下市场里都有可安装的内容。
/// </remarks>
public static class MarketCatalog
{
    public static string MarketDirectory => Path.Combine(AppContext.BaseDirectory, "market");

    /// <summary>扫描市场目录，返回可安装条目（坏包/无清单的直接跳过，不打断整体扫描）。</summary>
    public static IReadOnlyList<MarketEntry> Scan()
    {
        var directory = MarketDirectory;
        Directory.CreateDirectory(directory);
        var entries = new List<MarketEntry>();

        foreach (var zipPath in Directory.GetFiles(directory, "*.zip").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var manifest = ReadManifestFromZip(zipPath);
                if (manifest is null)
                {
                    continue;
                }

                entries.Add(new MarketEntry(
                    manifest.Id,
                    manifest.DisplayName,
                    manifest.Version,
                    manifest.Description ?? string.Empty,
                    zipPath,
                    "zip 插件包"));
            }
            catch
            {
                // 坏包跳过（市场是"浏览"入口，不该因单个坏文件整体失败）
            }
        }

        foreach (var sub in Directory.GetDirectories(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(sub).EndsWith(".shadow", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (!Directory.EnumerateFiles(sub, "*.dll").Any())
                {
                    continue;
                }

                var manifest = ReadManifestFromDirectory(sub);
                if (manifest is null)
                {
                    continue;
                }

                entries.Add(new MarketEntry(
                    manifest.Id,
                    manifest.DisplayName,
                    manifest.Version,
                    manifest.Description ?? string.Empty,
                    sub,
                    "目录插件"));
            }
            catch
            {
                // 同上：跳过
            }
        }

        return entries;
    }

    /// <summary>市场目录是否完全没有内容（用于决定是否播种）。</summary>
    public static bool IsEmpty()
    {
        var directory = MarketDirectory;
        if (!Directory.Exists(directory))
        {
            return true;
        }

        return !Directory.EnumerateFiles(directory, "*.zip").Any()
               && !Directory.EnumerateDirectories(directory).Any(d =>
                   !Path.GetFileName(d).EndsWith(".shadow", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 把一个已安装的目录插件打包成市场 zip，并改写 <c>plugin.json</c>：
    /// id 追加 <c>-market</c>、displayName 追加「（市场）」—— 避免与已安装的本体同 Id 撞车。
    /// 返回新 zip 路径；源插件没有清单时返回 null。
    /// </summary>
    public static string? SeedFromDirectory(string sourceDirectory)
    {
        var manifestPath = Path.Combine(sourceDirectory, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = ReadManifestFromDirectory(sourceDirectory);
        if (manifest is null)
        {
            return null;
        }

        Directory.CreateDirectory(MarketDirectory);

        var safeName = string.Join("_", manifest.DisplayName.Split(Path.GetInvalidFileNameChars()));
        var target = Path.Combine(MarketDirectory, $"{safeName}-seed.zip");
        var temp = target + ".tmp";

        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        ZipFile.CreateFromDirectory(sourceDirectory, temp, CompressionLevel.Optimal, includeBaseDirectory: false);

        // 改写包内 plugin.json
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Update))
        {
            var entry = FindManifestEntry(zip);
            if (entry is null)
            {
                zip.Dispose();
                File.Delete(temp);
                return null;
            }

            JsonObject? node;
            using (var reader = new StreamReader(entry.Open()))
            {
                node = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
            }

            if (node is null)
            {
                zip.Dispose();
                File.Delete(temp);
                return null;
            }

            node["id"] = manifest.Id + "-market";
            node["displayName"] = manifest.DisplayName + "（市场）";

            entry.Delete();
            var rewritten = zip.CreateEntry("plugin.json", CompressionLevel.Optimal);
            using var writer = new StreamWriter(rewritten.Open());
            writer.Write(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        File.Move(temp, target);
        return target;
    }

    // ── 清单读取 ──────────────────────────────────────────────────────────

    private sealed record Manifest(string Id, string DisplayName, string? Version, string? Description);

    private static Manifest? ReadManifestFromZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = FindManifestEntry(zip);
        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open());
        return ParseManifest(reader.ReadToEnd());
    }

    private static Manifest? ReadManifestFromDirectory(string directory)
    {
        var path = Path.Combine(directory, "plugin.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return ParseManifest(File.ReadAllText(path));
    }

    private static ZipArchiveEntry? FindManifestEntry(ZipArchive zip)
    {
        ZipArchiveEntry? root = null;
        ZipArchiveEntry? nested = null;

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (string.Equals(name, "plugin.json", StringComparison.OrdinalIgnoreCase))
            {
                root = entry;
            }
            else if (name.EndsWith("/plugin.json", StringComparison.OrdinalIgnoreCase) && nested is null)
            {
                nested = entry;
            }
        }

        return root ?? nested;
    }

    private static Manifest? ParseManifest(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject;
        if (node is null)
        {
            return null;
        }

        var id = node["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = node["displayName"]?.GetValue<string>();
        var version = node["version"]?.GetValue<string>();
        var description = node["description"]?.GetValue<string>();

        return new Manifest(id.Trim(), string.IsNullOrWhiteSpace(name) ? id.Trim() : name.Trim(), version, description);
    }
}
