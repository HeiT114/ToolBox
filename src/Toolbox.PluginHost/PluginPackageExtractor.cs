using System.IO.Compression;
using System.Security.Cryptography;

namespace Toolbox.PluginHost;

/// <summary>
/// zip 插件包 → 影子目录的解包器。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要解包，而不是 <c>AssemblyLoadContext.LoadFromStream</c> 直接读 zip？
/// <list type="bullet">
///   <item><see cref="System.Runtime.Loader.AssemblyDependencyResolver"/> 需要一个<strong>真实的入口 dll 路径</strong>，
///         它会在同目录找 <c>*.deps.json</c> —— 流式加载没有这个锚点，私有依赖会解析失败；</item>
///   <item>Windows 上被内存映射的 dll 在 ALC 回收前无法删除，影子目录正好给你"升级/回滚隔离"；</item>
///   <item>包内相对路径（satellite、<c>runtimes/&lt;rid&gt;/native</c>）必须落到磁盘才能被框架发现。</item>
/// </list>
/// </para>
/// <para>目录名 = <c>&lt;包名&gt;-&lt;内容哈希前12位&gt;</c>，因此同一版本重复安装是幂等的，不同版本天然隔离。</para>
/// </remarks>
internal static class PluginPackageExtractor
{
    private const string CompleteMarker = ".extracted";

    /// <summary>把 zip 解包（或复用已有解包结果），返回内容根目录。</summary>
    public static string Extract(string zipPath, string shadowRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(shadowRoot);

        var packageName = Path.GetFileNameWithoutExtension(zipPath);
        var hash = ComputeHash(zipPath);
        var target = Path.Combine(shadowRoot, $"{Sanitize(packageName)}-{hash}");

        if (File.Exists(Path.Combine(target, CompleteMarker)))
        {
            return ResolveContentRoot(target);
        }

        Directory.CreateDirectory(shadowRoot);
        if (Directory.Exists(target))
        {
            TryDeleteDirectory(target);
        }

        Directory.CreateDirectory(target);

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);

                // 打包工具的噪音
                if (relative.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Name, ".DS_Store", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(target, relative));

                // zip slip 防护：条目最终路径必须仍在 target 之内
                if (!destination.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(destination, target, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PluginLoadException($"zip 包包含越界条目，已拒绝解包：{entry.FullName}") { SourcePath = zipPath };
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        File.WriteAllText(Path.Combine(target, CompleteMarker), hash);
        return ResolveContentRoot(target);
    }

    /// <summary>删除已经不用的影子目录；被加载器占用的文件会返回 false（Windows 文件锁是预期行为）。</summary>
    public static bool TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveContentRoot(string extractedRoot) => SingleRootOrSelf(extractedRoot);

    /// <summary>很多包会把内容放在一层同名目录里，这里自动下钻一层。</summary>
    private static string SingleRootOrSelf(string extractedRoot)
    {
        var entries = Directory.GetFileSystemEntries(extractedRoot)
            .Where(e => !string.Equals(Path.GetFileName(e), CompleteMarker, StringComparison.Ordinal))
            .ToArray();

        return entries.Length == 1 && Directory.Exists(entries[0])
            ? entries[0]
            : extractedRoot;
    }

    private static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
