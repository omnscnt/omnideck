using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Omnisound.Services;

/// <summary>A newer release on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string PageUrl);

/// <summary>
/// Updates from the GitHub releases of omnscnt/omnisound: the latest release's win-x64 zip is downloaded,
/// unpacked to %TEMP%, and a small PowerShell script copies it over the install folder once this process
/// has exited, then starts the new version. Settings live elsewhere (%LOCALAPPDATA%) and are untouched.
/// </summary>
public static class Updater
{
    private const string LatestRelease = "https://api.github.com/repos/omnscnt/omnisound/releases/latest";
    private const string AssetSuffix = "-win-x64.zip";
    public const string UpdatedArgument = "--updated";

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient(); // after Current: its user agent names the version

    public static string CurrentText => Format(Current);

    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    /// <summary>
    /// Installing replaces the folder the exe runs from: only for a release install, never a build output
    /// (bin\...) of the source tree.
    /// </summary>
    public static bool CanInstall =>
        !AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("omnisound", CurrentText)); // GitHub requires one
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>The latest release if it is newer than this build, else null. Throws on network / API errors.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default)
    {
        using var response = await Http.GetAsync(LatestRelease, cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var root = json.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);
        if (version <= Current) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            return new UpdateInfo(
                version,
                tag,
                asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(),
                root.GetProperty("html_url").GetString() ?? "");
        }
        return null; // a release without a Windows build
    }

    /// <summary>
    /// Downloads and unpacks the update, then hands over to the swap script. The caller exits the app
    /// right after this returns: the script waits for that, copies the files and starts the new version.
    /// </summary>
    public static async Task PrepareAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancel = default)
    {
        string work = Path.Combine(Path.GetTempPath(), "omnisound-update");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        string zip = Path.Combine(work, "update.zip");
        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var file = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string unpacked = Path.Combine(work, "files");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpacked), cancel);
        // The zip holds one folder (omnisound-x.y.z-win-x64); accept files at the top level too.
        string root = Directory.GetFiles(unpacked).Length == 0 && Directory.GetDirectories(unpacked) is [var only] ? only : unpacked;
        if (!File.Exists(Path.Combine(root, "omnisound.exe"))) throw new InvalidDataException("The update package has no omnisound.exe.");

        string script = Path.Combine(work, "apply.ps1");
        await File.WriteAllTextAsync(script, SwapScript, cancel);
        string target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
                        $"-ProcessId {Environment.ProcessId} -Source \"{root}\" -Target \"{target}\" -Work \"{work}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    // Waits for omnisound to exit, copies the new files over the old ones (robocopy retries files still
    // locked for a moment), starts the result — the new version, or the old one if copying failed — and
    // cleans up. Extra files of older versions stay: the install folder may be shared.
    private const string SwapScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Work)
        try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction Stop } catch { }
        robocopy "$Source" "$Target" /E /R:20 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        Start-Process -FilePath (Join-Path $Target 'omnisound.exe') -ArgumentList '--updated'
        Start-Sleep -Seconds 3
        Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
        """;
}
