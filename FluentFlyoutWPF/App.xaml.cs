// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Windows;

namespace FluentFlyoutWPF;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // log unhandled exceptions before crashing
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Fatal(args.ExceptionObject as Exception, "Unhandled exception occurred");
            NLog.LogManager.Flush(); // Ensure logs are written before application dies
        };

        DisableDebuggerLoggingWhenDetached();

        // Register AUMID for toast notifications
        ToastNotificationManagerCompat.OnActivated += Notifications.HandleNotificationActivation;

        // Note: the upstream A/B experiments fetch used to be awaited here, blocking startup on a
        // network round-trip (up to 2s) to the upstream server. This fork doesn't take part in those
        // experiments, so it is skipped; ExperimentsService then falls back to the default variants.

        base.OnStartup(e);
    }

    /// <summary>
    /// NLog.config sends Trace/Debug messages to the debugger output. Without a debugger attached that output
    /// goes nowhere, but the rule still makes every Debug/Trace call format its message and queue it.
    /// </summary>
    private static void DisableDebuggerLoggingWhenDetached()
    {
        if (System.Diagnostics.Debugger.IsAttached)
            return;

        try
        {
            var config = NLog.LogManager.Configuration;
            if (config == null)
                return;

            var debuggerRules = config.LoggingRules
                .Where(rule => rule.Targets.Any(target => target.Name == "debugger"))
                .ToList();

            if (debuggerRules.Count == 0)
                return;

            foreach (var rule in debuggerRules)
                config.LoggingRules.Remove(rule);

            NLog.LogManager.ReconfigExistingLoggers();
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Failed to disable debugger logging");
        }
    }
}