// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.ViewModels;
using NLog;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace FluentFlyoutWPF.Classes.Services;

/// <summary>
/// Checks GitHub Releases for new versions and installs them with the release's setup executable.
/// </summary>
public static class UpdateCheckerService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}/releases/latest";
    private const string ReleaseDownloadPrefix = $"{AppInfo.RepositoryUrl}/releases/download/";
    private const string LatestManifestUrl = $"{AppInfo.RepositoryUrl}/releases/latest/download/latest.json";

    // /UPDATE=1 tells the installer to relaunch the app after a silent install
    private const string SilentInstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /UPDATE=1";

    private static readonly TimeSpan InitialCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailedInstallRetryDelay = TimeSpan.FromDays(1);

    private static readonly string DownloadDirectory = Path.Combine(Path.GetTempPath(), "FluentFlyoutFree-Update");

    private static readonly Lazy<HttpClient> Http = new(CreateHttpClient);
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static Timer? _backgroundTimer;

    /// <summary>
    /// Result of an update check
    /// </summary>
    public class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; set; }
        public string NewestVersion { get; set; } = string.Empty;

        /// <summary>
        /// The GitHub release page of the newest version.
        /// </summary>
        public string UpdateUrl { get; set; } = string.Empty;
        public string InstallerUrl { get; set; } = string.Empty;
        public string InstallerName { get; set; } = string.Empty;
        public long InstallerSize { get; set; }
        public string? InstallerSha256 { get; set; }
        public DateTime CheckedAt { get; set; }
        public bool Success { get; set; }

        public bool HasInstaller => !string.IsNullOrEmpty(InstallerUrl);
    }

    /// <summary>
    /// The most recent successful update check, used to install the update on demand.
    /// </summary>
    public static UpdateCheckResult? LatestResult { get; private set; }

    /// <summary>
    /// Whether this copy can update itself: it must be installed by the setup (not MSIX) and be a versioned build.
    /// </summary>
    public static bool CanSelfUpdate => !AppInfo.IsPackaged && AppInfo.Version != "debug";

    public static bool CanInstall(UpdateCheckResult? result) =>
        CanSelfUpdate && result is { IsUpdateAvailable: true, HasInstaller: true };

    /// <summary>
    /// Starts the periodic background update check (first check shortly after startup, then every few hours).
    /// </summary>
    public static void StartBackgroundChecks()
    {
        if (_backgroundTimer != null)
            return;

        _ = Task.Run(CleanupDownloadDirectory);
        _backgroundTimer = new Timer(_ => _ = RunBackgroundCheckAsync(), null, InitialCheckDelay, CheckInterval);
    }

    public static void StopBackgroundChecks()
    {
        _backgroundTimer?.Dispose();
        _backgroundTimer = null;
    }

    private static async Task RunBackgroundCheckAsync()
    {
        try
        {
            var result = await CheckForUpdatesAsync(AppInfo.Version);
            if (!result.Success || !result.IsUpdateAvailable)
                return;

            if (SettingsManager.Current.AutoInstallUpdates && CanInstall(result) && !RecentlyAttempted(result.NewestVersion))
            {
                Logger.Info($"Automatically installing update {result.NewestVersion}");
                if (await DownloadAndInstallAsync(result))
                    return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
                Notifications.ShowUpdateAvailableNotification(result.NewestVersion, result.UpdateUrl));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Background update check failed");
        }
    }

    /// <summary>
    /// Check GitHub for the latest release and publish the result to <see cref="UpdateState"/>.
    /// </summary>
    /// <param name="currentVersion">The current app version (e.g., "v2.5.0")</param>
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var result = new UpdateCheckResult
        {
            CheckedAt = DateTime.Now,
            UpdateUrl = AppInfo.ReleasesUrl
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ApiTimeout);

            // 1. the release manifest asset: a plain download, so it isn't subject to the
            //    GitHub API limit of 60 requests/hour per IP (easily exhausted behind shared IPs)
            // 2. the GitHub REST API, for releases published without a manifest
            if (!await TryReadReleaseManifestAsync(result, timeout.Token))
                await ReadLatestReleaseFromApiAsync(result, timeout.Token);

            if (result.Success)
            {
                result.IsUpdateAvailable = currentVersion != "debug"
                    && !string.IsNullOrEmpty(result.NewestVersion)
                    && IsNewerVersion(currentVersion, result.NewestVersion);

                Logger.Info($"Update check complete. Current: {currentVersion}, Newest: {result.NewestVersion}, Update available: {result.IsUpdateAvailable}, Installer: {result.InstallerName}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Info("Failed to check for updates - request timed out.");
        }
        catch (HttpRequestException ex)
        {
            Logger.Info($"Failed to check for updates - network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected error checking for updates");
        }

        if (result.Success)
        {
            LatestResult = result;
            await PublishStateAsync(result);
        }

        return result;
    }

    /// <summary>
    /// Reads latest.json from the latest release, written by installer/build-installer.ps1:
    /// { "version": "2.16.2", "installers": { "x64": { "name": "...", "size": 123, "sha256": "..." }, ... } }
    /// </summary>
    /// <returns>False if the latest release has no manifest (or there is no release), so the API should be tried.</returns>
    private static async Task<bool> TryReadReleaseManifestAsync(UpdateCheckResult result, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.Value.GetAsync(LatestManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Debug($"Release manifest not available ({(int)response.StatusCode})");
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = json.RootElement;

            string version = root.TryGetProperty("version", out var versionProp) ? versionProp.GetString() ?? string.Empty : string.Empty;
            if (!TryParseVersion(version, out _))
            {
                Logger.Warn($"Release manifest has an invalid version: '{version}'");
                return false;
            }

            string tag = "v" + version.Trim().TrimStart('v', 'V');
            result.NewestVersion = tag;
            result.UpdateUrl = $"{AppInfo.ReleasesUrl}/tag/{tag}";

            if (root.TryGetProperty("installers", out var installers)
                && installers.ValueKind == JsonValueKind.Object
                && installers.TryGetProperty(CurrentArchitecture, out var installer))
            {
                string name = installer.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;

                // the download URL is always built from this repository + tag, never taken from the manifest
                if (IsSafeAssetName(name))
                {
                    result.InstallerName = name;
                    result.InstallerUrl = $"{ReleaseDownloadPrefix}{tag}/{name}";
                    result.InstallerSize = installer.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0;
                    result.InstallerSha256 = installer.TryGetProperty("sha256", out var sha) ? sha.GetString() : null;
                }
            }

            result.Success = true;
            return true;
        }
        catch (JsonException ex)
        {
            Logger.Warn(ex, "Release manifest is not valid JSON");
            return false;
        }
    }

    private static async Task ReadLatestReleaseFromApiAsync(UpdateCheckResult result, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await Http.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // no published (non-prerelease) release yet
            Logger.Info("No GitHub release published yet");
            result.Success = true;
            return;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;

        result.NewestVersion = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (root.TryGetProperty("html_url", out var htmlUrl) && htmlUrl.GetString() is { Length: > 0 } url)
            result.UpdateUrl = url;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            SelectInstallerAsset(assets, result);

        result.Success = true;
    }

    private static string CurrentArchitecture =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    private static bool IsSafeAssetName(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(['/', '\\', '?', '#', '%', ':']) < 0
        && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Downloads the installer of <paramref name="update"/>, verifies it, runs it silently and exits the app.
    /// </summary>
    /// <returns>False if the update could not be downloaded or started.</returns>
    public static async Task<bool> DownloadAndInstallAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!CanInstall(update))
            return false;

        if (!await InstallLock.WaitAsync(0, cancellationToken))
        {
            Logger.Info("Update installation already in progress");
            return false;
        }

        try
        {
            string installerPath = await DownloadInstallerAsync(update, progress, cancellationToken);

            // remember the attempt so a broken release can't put the app in an install loop
            SettingsManager.Current.LastUpdateAttemptVersion = update.NewestVersion;
            SettingsManager.Current.LastUpdateAttemptUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SettingsManager.SaveSettings();

            Logger.Info($"Starting installer for {update.NewestVersion}: {installerPath}");
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = SilentInstallerArguments,
                WorkingDirectory = DownloadDirectory,
                UseShellExecute = true
            });

            // the installer waits for this process to exit before replacing files, then relaunches the app
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (Application.Current.MainWindow is MainWindow mainWindow)
                    mainWindow.ShutdownForUpdate();
                else
                    Application.Current.Shutdown();
            });

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to install update {update.NewestVersion}");
            return false;
        }
        finally
        {
            InstallLock.Release();
        }
    }

    private static async Task<string> DownloadInstallerAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DownloadDirectory);

        string installerPath = Path.Combine(DownloadDirectory, Path.GetFileName(update.InstallerName));
        string partialPath = installerPath + ".part";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        using var response = await Http.Value.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();

        long expectedSize = response.Content.Headers.ContentLength ?? update.InstallerSize;
        long totalRead = 0;
        byte[] hash;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    sha256.AppendData(buffer, 0, read);
                    totalRead += read;

                    if (expectedSize > 0)
                        progress?.Report(Math.Min(1.0, (double)totalRead / expectedSize));
                }
            }

            hash = sha256.GetHashAndReset();
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (update.InstallerSize > 0 && totalRead != update.InstallerSize)
        {
            TryDelete(partialPath);
            throw new InvalidDataException($"Installer size mismatch: expected {update.InstallerSize} bytes, got {totalRead}");
        }

        if (!string.IsNullOrEmpty(update.InstallerSha256)
            && !string.Equals(Convert.ToHexString(hash), update.InstallerSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partialPath);
            throw new InvalidDataException("Installer SHA-256 does not match the digest published by GitHub");
        }

        File.Move(partialPath, installerPath, overwrite: true);
        return installerPath;
    }

    private static void SelectInstallerAsset(JsonElement assets, UpdateCheckResult result)
    {
        // assets are named like "FluentFlyoutFree-Setup-x64.exe" / "FluentFlyoutFree-Setup-arm64.exe"
        string suffix = $"-{CurrentArchitecture}.exe";

        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
            if (!name.Contains("Setup", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            string url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() ?? string.Empty : string.Empty;

            // only ever download from this repository's releases
            if (!url.StartsWith(ReleaseDownloadPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn($"Ignoring installer asset with unexpected URL: {url}");
                continue;
            }

            result.InstallerName = name;
            result.InstallerUrl = url;
            result.InstallerSize = asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0;

            // GitHub publishes "sha256:<hex>" digests for release assets
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } digestValue
                && digestValue.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                result.InstallerSha256 = digestValue["sha256:".Length..];
            }

            return;
        }
    }

    private static bool RecentlyAttempted(string version)
    {
        var settings = SettingsManager.Current;
        if (!string.Equals(settings.LastUpdateAttemptVersion, version, StringComparison.OrdinalIgnoreCase))
            return false;

        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - settings.LastUpdateAttemptUnixSeconds;
        if (elapsed >= FailedInstallRetryDelay.TotalSeconds)
            return false;

        Logger.Warn($"Update {version} was already installed recently but the app still reports {AppInfo.Version}; not retrying automatically");
        return true;
    }

    private static async Task PublishStateAsync(UpdateCheckResult result)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdateState.Current.IsUpdateAvailable = result.IsUpdateAvailable;
                UpdateState.Current.NewestVersion = result.NewestVersion;
                UpdateState.Current.UpdateUrl = result.UpdateUrl;
                UpdateState.Current.LastUpdateCheck = result.CheckedAt;
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to publish update state");
        }
    }

    public static void OpenUpdateUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open update URL");
        }
    }

    private static bool IsNewerVersion(string currentVersion, string newestVersion)
    {
        if (TryParseVersion(currentVersion, out var current) && TryParseVersion(newestVersion, out var newest))
            return newest > current;

        Logger.Error($"Failed to compare versions: {currentVersion} vs {newestVersion}");
        return false;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        // accepts "v2.16.1", "2.16.1", "v2.16.1-beta"
        string trimmed = value.Trim().TrimStart('v', 'V');
        int suffixIndex = trimmed.IndexOfAny(['-', '+', ' ']);
        if (suffixIndex >= 0)
            trimmed = trimmed[..suffixIndex];

        return Version.TryParse(trimmed, out version!);
    }

    private static void CleanupDownloadDirectory()
    {
        try
        {
            if (Directory.Exists(DownloadDirectory))
                Directory.Delete(DownloadDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not clean up the update download directory");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // close idle connections quickly: checks only happen every few hours
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            // per-request timeouts are applied with cancellation tokens
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"FluentFlyoutFree/{AppInfo.Version.TrimStart('v')}");
        return client;
    }
}