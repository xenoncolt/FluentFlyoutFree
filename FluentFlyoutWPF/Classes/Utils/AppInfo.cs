// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;

namespace FluentFlyoutWPF.Classes.Utils;

/// <summary>
/// Version and distribution information about the running build.
/// </summary>
public static class AppInfo
{
    public const string GitHubOwner = "xenoncolt";
    public const string GitHubRepo = "FluentFlyoutFree";

    public const string RepositoryUrl = $"https://github.com/{GitHubOwner}/{GitHubRepo}";
    public const string ReleasesUrl = $"{RepositoryUrl}/releases";
    public const string IssuesUrl = $"{RepositoryUrl}/issues/new/choose";

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    /// <summary>
    /// True when running as an MSIX package (Store / sideloaded), false for the installer or a plain build.
    /// </summary>
    public static bool IsPackaged { get; } = DetectIsPackaged();

    /// <summary>
    /// The app version formatted as "vMajor.Minor.Build", or "debug" for Debug builds.
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
#if DEBUG
        return "debug";
#else
        if (IsPackaged)
        {
            try
            {
                var packageVersion = global::Windows.ApplicationModel.Package.Current.Id.Version;
                return $"v{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}";
            }
            catch
            {
                // fall through to the assembly version
            }
        }

        // set from <Version> in the csproj, which the release workflow overrides with the git tag
        var assemblyVersion = Assembly.GetEntryAssembly()?.GetName().Version;
        return assemblyVersion is { Major: > 0 } v
            ? $"v{v.Major}.{v.Minor}.{v.Build}"
            : "debug";
#endif
    }

    private static bool DetectIsPackaged()
    {
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}