// Lockwell - local-only encrypted vault
// Copyright (C) 2026 Lockwell
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Lockwell.Installer.Services;

internal static class GitHubReleaseClient
{
    public const string DefaultOwner = "slashneck";
    public const string DefaultRepo = "Lockwell";
    public const string ReleaseZipAssetFileName = "Lockwell-win-x64.zip";

    private static readonly HttpClient Http = CreateHttpClient();

    public sealed record ReleaseCheckResult(Version LatestVersion, string TagName, string DownloadUrl);

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var version = ReadAppVersion()?.ToString() ?? "1.0.0";
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LockwellSetup", version));
        return c;
    }

    public static Version? ReadAppVersion()
    {
        try
        {
            return (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<ReleaseCheckResult?> CheckLatestReleaseAsync(CancellationToken ct = default)
    {
        var parsed = await ResolveLatestReleaseAsync(ct).ConfigureAwait(false);
        if (parsed is null)
        {
            return null;
        }

        return new ReleaseCheckResult(parsed.Version, parsed.TagName, parsed.DownloadUrl);
    }

    /// <summary>
    /// GitHub <c>/releases/latest</c> excludes pre-releases. Fall back to the newest published
    /// non-draft release (including pre-releases) so beta tags still work.
    /// </summary>
    private static async Task<ParsedRelease?> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        var latestUrl = $"https://api.github.com/repos/{DefaultOwner}/{DefaultRepo}/releases/latest";
        var parsed = await TryParseReleaseFromGetAsync(latestUrl, ct).ConfigureAwait(false);
        if (parsed is not null)
        {
            return parsed;
        }

        var listUrl = $"https://api.github.com/repos/{DefaultOwner}/{DefaultRepo}/releases";
        using var resp = await Http.GetAsync(listUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            parsed = ParseReleaseElement(rel);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private static async Task<ParsedRelease?> TryParseReleaseFromGetAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseReleaseElement(doc.RootElement);
    }

    private sealed record ParsedRelease(Version Version, string TagName, string DownloadUrl);

    private static ParsedRelease? ParseReleaseElement(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tagEl))
        {
            return null;
        }

        var tag = tagEl.GetString() ?? string.Empty;
        var latest = ParseVersionFromTag(tag);
        var downloadUrl = PickZipAssetUrl(root);
        if (latest is null || string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        return new ParsedRelease(latest, tag.Trim(), downloadUrl);
    }

    public static async Task<string?> DownloadReleaseZipAsync(
        string downloadUrl,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var dest = Path.Combine(Path.GetTempPath(), "Lockwell", $"setup-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        using var resp = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var total = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(dest);

        var buffer = new byte[81920];
        long read = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            read += count;
            if (total is > 0)
            {
                progress?.Report(read / (double)total.Value * 100.0);
            }
        }

        progress?.Report(100);
        return dest;
    }

    public static bool InstallReleaseZipToDirectory(string zipPath, string targetDirectory)
    {
        if (!File.Exists(zipPath))
        {
            return false;
        }

        var extractRoot = Path.Combine(Path.GetTempPath(), "Lockwell", "extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot);

            var sourceDir = FindFolderContainingExe(extractRoot, InstallPaths.ExecutableFileName);
            if (string.IsNullOrEmpty(sourceDir))
            {
                return false;
            }

            Directory.CreateDirectory(targetDirectory);
            return MirrorDirectory(sourceDir, targetDirectory);
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractRoot))
                {
                    Directory.Delete(extractRoot, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool MirrorDirectory(string sourceDir, string targetDirectory)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "robocopy",
            Arguments = $"\"{sourceDir}\" \"{targetDirectory}\" /E /IS /IT /NFL /NDL /NJH /NJS",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (proc is null)
        {
            return false;
        }

        proc.WaitForExit();
        return proc.ExitCode is >= 0 and < 8;
    }

    private static string? FindFolderContainingExe(string extractRoot, string exeName)
    {
        try
        {
            var hits = Directory.GetFiles(extractRoot, exeName, SearchOption.AllDirectories);
            return hits.Length == 0 ? null : Path.GetDirectoryName(hits[0]);
        }
        catch
        {
            return null;
        }
    }

    private static string? PickZipAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? firstZip = null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var nameEl) || !a.TryGetProperty("browser_download_url", out var urlEl))
            {
                continue;
            }

            var name = nameEl.GetString() ?? string.Empty;
            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            firstZip ??= url;
            if (string.Equals(name, ReleaseZipAssetFileName, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return firstZip;
    }

    private static Version? ParseVersionFromTag(string tag)
    {
        var s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            s = s[..dash];
        }

        return Version.TryParse(s, out var v) ? v : null;
    }
}
