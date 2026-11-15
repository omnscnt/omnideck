using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Omnideck.Services;

/// <summary>A newer release on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string PageUrl);

/// <summary>
/// Updates from the GitHub releases of omnscnt/omnideck: the latest release's win-x64 zip is downloaded,
/// unpacked to a staging folder, and a small PowerShell script copies it over the install folder once this process
/// has exited, then starts the new version. Settings live elsewhere (%LOCALAPPDATA%) and are untouched.
/// </summary>
public static class Updater
{
    private const string LatestRelease = "https://api.github.com/repos/omnscnt/omnideck/releases/latest";
    private const string AssetSuffix = "-win-x64.zip";
    public const string UpdatedArgument = "--updated";
    internal const string PrepareArgument = "--prepare-protected-update";

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
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("omnideck", CurrentText)); // GitHub requires one
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
        var operation = Guid.NewGuid();
        if (AdministratorTask.IsInProgramFiles && !IsAdministrator())
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = InstallLayout.AppDirectory,
                ArgumentList = { PrepareArgument, Environment.ProcessId.ToString(), operation.ToString("N"), Format(update.Version) },
            };
            try
            {
                using var helper = Process.Start(info) ?? throw new IOException("Could not start the update installer.");
                await helper.WaitForExitAsync();
                if (helper.ExitCode != 0) throw new IOException("The update installer could not prepare the update. See the Omnideck log.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("Administrator approval was cancelled.", ex);
            }

            // Start the restart process with the original token before the app shuts down.
            var restart = PowerShell();
            restart.ArgumentList.Add("-EncodedCommand");
            restart.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(RestartScript)));
            restart.Environment["OMNIDECK_UPDATE_DIRECTORY"] = WorkDirectory(operation);
            restart.Environment["OMNIDECK_UPDATE_LAUNCHER"] = Path.Combine(InstallLayout.Root, InstallLayout.LauncherName);
            restart.Environment["OMNIDECK_UPDATE_VERSION"] = Format(update.Version);
            using var watcher = Process.Start(restart) ?? throw new IOException("Could not start the update restart process.");
            return;
        }

        await PrepareUpdateAsync(update, WorkDirectory(operation), Environment.ProcessId, restartAfterSwap: true, progress, cancel);
    }

    internal static async Task PrepareProtectedAsync(int callerId, Guid operation, Version requestedVersion)
    {
        if (!AdministratorTask.IsInProgramFiles || !IsAdministrator())
            throw new UnauthorizedAccessException("The installer needs administrator rights for Program Files.");
        var update = await CheckAsync();
        if (update == null || update.Version != requestedVersion)
            throw new InvalidOperationException("The available update changed. Check for updates again.");
        // Fetch the release in the elevated process so the script and payload never pass through a writable staging area.
        await PrepareUpdateAsync(update, WorkDirectory(operation), callerId, restartAfterSwap: false, null, default);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string WorkDirectory(Guid operation) => Path.Combine(InstallLayout.Root, $"omnideck-update-{operation:N}");

    private static async Task PrepareUpdateAsync(UpdateInfo update, string work, int callerId, bool restartAfterSwap,
        IProgress<double>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(work);
        try
        {
            await StageUpdateAsync(update, work, callerId, restartAfterSwap, progress, cancel);
        }
        catch
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static async Task StageUpdateAsync(UpdateInfo update, string work, int callerId, bool restartAfterSwap,
        IProgress<double>? progress, CancellationToken cancel)
    {
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
        // The zip holds one folder (omnideck-x.y.z-win-x64); accept files at the top level too.
        string root = Directory.GetFiles(unpacked).Length == 0 && Directory.GetDirectories(unpacked) is [var only] ? only : unpacked;
        if (!File.Exists(Path.Combine(root, "omnideck.exe"))) throw new InvalidDataException("The update package has no omnideck.exe.");
        if (AdministratorTask.IsEnabled) AdministratorTask.SecureUpdatePayload(root);

        string script = Path.Combine(work, "apply.ps1");
        await File.WriteAllTextAsync(script, SwapScript, cancel);
        var apply = PowerShell();
        foreach (string argument in new[] { "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ProcessId", callerId.ToString(), "-Source", root, "-Target", InstallLayout.Root, "-Work", work })
            apply.ArgumentList.Add(argument);
        if (restartAfterSwap) apply.ArgumentList.Add("-Restart");
        using var swap = Process.Start(apply) ?? throw new IOException("Could not start the update installer.");
    }

    private static ProcessStartInfo PowerShell() => new()
    {
        FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        WorkingDirectory = InstallLayout.Root,
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden" },
    };

    private const string RestartScript = """
        $work = $env:OMNIDECK_UPDATE_DIRECTORY
        $launcher = $env:OMNIDECK_UPDATE_LAUNCHER
        $deadline = (Get-Date).AddMinutes(3)
        while ((Test-Path -LiteralPath $work) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
        if (Test-Path -LiteralPath $work) { exit 1 }
        $version = [Reflection.AssemblyName]::GetAssemblyName($launcher).Version.ToString(3)
        $argument = if ($version -eq $env:OMNIDECK_UPDATE_VERSION) { '--updated' } else { '--autostart' }
        Start-Process -FilePath $launcher -ArgumentList $argument
        """;

    private const string SwapScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Work, [switch]$Restart)
        $ErrorActionPreference = 'Stop'
        $launcher = Join-Path $Target 'omnideck.exe'
        $app = Join-Path $Target 'app'
        $newApp = Join-Path $Source 'app'
        $backupApp = Join-Path $Work 'previous-app'
        $updated = $false
        $parent = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        try {
            if ($parent) { Wait-Process -InputObject $parent -Timeout 60 }
            if (Test-Path -LiteralPath $newApp) {
                if (Test-Path -LiteralPath $app) { [IO.Directory]::Move($app, $backupApp) }
                try {
                    [IO.Directory]::Move($newApp, $app)
                    try {
                        $newLauncher = Join-Path $Source 'omnideck.exe'
                        if (Test-Path -LiteralPath $launcher) {
                            [IO.File]::Replace($newLauncher, $launcher, (Join-Path $Work 'previous-launcher.exe'))
                        } else {
                            [IO.File]::Move($newLauncher, $launcher)
                        }
                    } catch {
                        [IO.Directory]::Move($app, $newApp)
                        throw
                    }
                } catch {
                    if (Test-Path -LiteralPath $backupApp) { [IO.Directory]::Move($backupApp, $app) }
                    throw
                }
            } else {
                & (Join-Path ([Environment]::SystemDirectory) 'robocopy.exe') $Source $Target /E /R:20 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
                if ($LASTEXITCODE -ge 8) { throw "Robocopy failed with exit code $LASTEXITCODE." }
            }
            $updated = $true
        } catch {
            $_ | Out-String | Set-Content -LiteralPath (Join-Path $Target 'omnideck-update-error.log')
            if (Test-Path -LiteralPath $backupApp) { exit 1 }
        }
        Remove-Item -LiteralPath $Work -Recurse -Force
        if ($Restart) {
            $argument = if ($updated) { '--updated' } else { '--autostart' }
            Start-Process -FilePath $launcher -ArgumentList $argument
        }
        """;
}
