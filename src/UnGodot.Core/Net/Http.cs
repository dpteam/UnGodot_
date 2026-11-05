using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using UnGodot.Core.IO;
using UnGodot.Core.Logging;

namespace UnGodot.Core.Net;

/// <summary>Small HTTP helpers shared by the parts of the toolkit that fetch things.</summary>
public static class Http
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ungodot");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    /// <summary>Where downloaded tools and editor builds are kept.</summary>
    public static string DataDirectory
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable("UNGODOT_HOME");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            if (OperatingSystem.IsWindows())
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                {
                    return Path.Combine(local, "ungodot");
                }
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? Path.Combine(".", "ungodot") : Path.Combine(home, ".local", "share", "ungodot");
        }
    }

    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");

    public static async Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new IOException($"cannot fetch {url}: {ex.Message}", ex);
        }
    }

    public static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        string text = await GetTextAsync(url, cancellationToken);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new IOException($"cannot parse the JSON from {url}: {ex.Message}", ex);
        }
    }

    public static async Task<bool> ExistsAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Downloads into the cache, reusing an earlier complete download.</summary>
    public static async Task<string> FetchCachedAsync(string url, string name, CancellationToken cancellationToken)
    {
        string directory = CacheDirectory;
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, name);
        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            Log.Debug($"using the cached {name}");
            return target;
        }

        await DownloadAsync(url, target, cancellationToken);
        return target;
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="target"/> through a .part file.</summary>
    public static async Task DownloadAsync(string url, string target, CancellationToken cancellationToken)
    {
        Log.Info($"downloading {url}");
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        string partial = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target)) ?? ".");

        long done = 0;
        long lastLog = 0;
        var watch = Stopwatch.StartNew();

        await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            byte[] buffer = new byte[1 << 16];
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;

                if (watch.ElapsedMilliseconds - lastLog > 2000)
                {
                    lastLog = watch.ElapsedMilliseconds;
                    Log.Info(total is > 0
                        ? $"{Path.GetFileName(target)}: {PathUtil.HumanSize(done)} / {PathUtil.HumanSize(total.Value)} ({done * 100 / total.Value}%)"
                        : $"{Path.GetFileName(target)}: {PathUtil.HumanSize(done)}");
                }
            }
        }

        File.Move(partial, target, overwrite: true);

        if (total is > 0 && done != total.Value)
        {
            throw new IOException($"incomplete download of {url} ({done} of {total} bytes)");
        }
    }

    /// <summary>Extracts a zip archive (used for GDRE Tools, editors and plugins).</summary>
    public static void ExtractZip(string archive, string destination, bool overwrite = true)
    {
        Directory.CreateDirectory(destination);
        using var zip = System.IO.Compression.ZipFile.OpenRead(PathUtil.FullPath(archive));
        foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
        {
            string? relative = SafeEntryName(entry.FullName);
            if (relative is null)
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(PathUtil.WithLongPathPrefix(target), overwrite);
        }
    }

    private static string? SafeEntryName(string name)
    {
        string normalized = name.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p == ".." || p == "."))
        {
            return null;
        }

        return Path.Combine(parts);
    }
}