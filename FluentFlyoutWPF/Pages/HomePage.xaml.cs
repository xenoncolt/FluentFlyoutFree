// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Services;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.ViewModels;
using NLog;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace FluentFlyoutWPF.Pages;

public partial class HomePage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public HomePage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;

        VersionTextBlock.Text = AppInfo.Version == "debug" ? "debug version" : AppInfo.Version;

        UpdateLastCheckedText();
    }

    private void UpdateLastCheckedText()
    {
        if (UpdateState.Current.LastUpdateCheck != default)
        {
            LastCheckedText.Text = string.Format(
                Application.Current.FindResource("LastChecked")?.ToString(),
                UpdateState.Current.LastCheckedText);
        }
        else
        {
            LastCheckedText.Text = string.Empty;
        }
    }

    private void ViewUpdates_Click(object sender, RoutedEventArgs e)
    {
        Notifications.OpenChangelogInBrowser();
    }

    private long _lastChecked = 0;

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        // prevent multiple clicks within 1 second
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastChecked < 1)
        {
            return;
        }

        _lastChecked = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (_isInstalling)
            return;

        if (UpdateState.Current.IsUpdateAvailable)
        {
            if (UpdateCheckerService.CanInstall(UpdateCheckerService.LatestResult))
            {
                await InstallUpdateAsync(UpdateCheckerService.LatestResult!);
                return;
            }

            string url = !string.IsNullOrEmpty(UpdateState.Current.UpdateUrl) ? UpdateState.Current.UpdateUrl : AppInfo.ReleasesUrl;
            UpdateCheckerService.OpenUpdateUrl(url);
        }
        else
        {
            await CheckForUpdatesAsync();
        }
    }

    private bool _isInstalling;

    private async Task InstallUpdateAsync(UpdateCheckerService.UpdateCheckResult update)
    {
        _isInstalling = true;
        string downloadingFormat = Application.Current.TryFindResource("DownloadingUpdate") as string ?? "Downloading update... {0}%";

        try
        {
            var progress = new Progress<double>(value =>
                UpdateStatusText.Text = string.Format(downloadingFormat, (int)(value * 100)));

            UpdateStatusText.Text = string.Format(downloadingFormat, 0);
            bool started = await UpdateCheckerService.DownloadAndInstallAsync(update, progress);

            if (started)
            {
                UpdateStatusText.Text = Application.Current.TryFindResource("InstallingUpdate") as string ?? "Installing update...";
                return; // the app is shutting down
            }

            UpdateStatusText.Text = Application.Current.TryFindResource("UpdateInstallFailed") as string ?? "Update failed. Opening the release page...";
            UpdateCheckerService.OpenUpdateUrl(update.UpdateUrl);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to install update from HomePage");
        }
        finally
        {
            _isInstalling = false;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            UpdateStatusText.Text = Application.Current.FindResource("CheckingForUpdates")?.ToString();

            // also publishes the result to UpdateState
            var result = await UpdateCheckerService.CheckForUpdatesAsync(AppInfo.Version);

            if (result.Success)
            {
                UpdateLastCheckedText();

                _ = Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(500); // slight delay for better UX

                        if (result.IsUpdateAvailable)
                        {
                            UpdateStatusText.Text = Application.Current.FindResource("UpdateAvailableNotificationTitle")?.ToString();
                        }
                        else
                        {
                            UpdateStatusText.Text = Application.Current.FindResource("UpToDate")?.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Error while updating update status text on UI thread");
                    }
                });
            }
            else
            {
                UpdateStatusText.Text = Application.Current.FindResource("UpToDate")?.ToString();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for updates from HomePage");
            UpdateStatusText.Text = "Unable to check for updates"; // not localized
        }
    }

    private void MediaFlyout_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(MediaFlyoutPage));
    }

    private void VolumeFlyout_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(VolumeMixerPage));
    }

    private void TaskbarWidget_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(TaskbarWidgetPage));
    }

    private void NextUp_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(NextUpPage));
    }

    private void LockKeys_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(LockKeysPage));
    }

    private void TaskbarVisualizer_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(TaskbarVisualizerPage));
    }

    private void System_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(SystemPage));
    }

    private void ViewLogs_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Process.Start("explorer.exe", FileSystemHelper.GetLogsPath());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open logs folder");
        }
    }

    private void ReportBug_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppInfo.IssuesUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open bug report page");
        }
    }
}