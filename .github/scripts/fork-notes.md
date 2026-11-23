# How FluentFlyout Free differs from upstream FluentFlyout

The weekly sync gives this file to the AI when it fixes merge conflicts, build errors or premium code.
Plain English is fine. If the AI keeps getting something wrong, add a line here.

## Everything is free

- There is no premium, no purchase and no Microsoft Store licensing. These were deleted:
  `LicenseManager`, `PremiumPurchaseButton`, `PremiumUnlockedBanner`, `AccentBadge`,
  `IsPremiumUnlockedBannerOpen`, `PremiumUnlockedBannerClosedOnce`.
- `SettingsManager.Current.IsPremiumUnlocked` always returns `true` and `IsStoreVersion` always returns `false`.
  Code that checks them compiles and behaves as "unlocked".
- Never bring back purchase buttons, "Premium" badges or labels, upgrade prompts, locked settings,
  `StoreContext` purchase calls or `LicenseManager`. When upstream code uses one of the deleted premium
  pieces, remove that usage and keep the feature itself available to everyone.
- Settings that upstream only allows for premium users (for example the acrylic blur opacity) always use the
  user's chosen value here.

## Installer build, own version numbers, own updates

- The app ships as a normal (unpackaged) installer made with Inno Setup, not as MSIX/Microsoft Store.
- Get the app version from `AppInfo.Version` ("vMajor.Minor.Build", or "debug" in Debug builds) and use
  `AppInfo.IsPackaged` to tell packaged from installed. Never use `Windows.ApplicationModel.Package.Current`
  (it throws when the app isn't packaged).
- This fork has its own version numbers (v1.x), unrelated to upstream's.
- Updates come from this fork's GitHub releases: `UpdateCheckerService.StartBackgroundChecks()` at startup and
  `UpdateCheckerService.StopBackgroundChecks()` on cleanup (they download and install updates silently).
  Don't bring back upstream's `CheckForUpdatesOnStartupAsync` or any Store update logic.
- `MainWindow.ShutdownForUpdate()` belongs to this fork's updater; keep it.

## Links and texts

- Links to the repository, releases, issues and changelog use `AppInfo.RepositoryUrl`, `AppInfo.ReleasesUrl`
  and `AppInfo.IssuesUrl`, not upstream's GitHub URLs or the fluentflyout.com website pages
  (`Notifications.OpenChangelogInBrowser()` opens this fork's releases page).

## Startup and performance

- Upstream's `ExperimentsService.GetExperimentsAsync()` is not called (A/B experiments from upstream's
  server are skipped so startup doesn't wait on the network).
- `App.DisableDebuggerLoggingWhenDetached()` and the precomputed buffers in `Visualizer` are this fork's
  performance tweaks; keep them.
- The "start with Windows" registry value quotes the executable path.

## Repository files

- This fork's own files: `.github/workflows/release.yml`, `.github/workflows/sync-upstream.yml`,
  `.github/scripts/*`, `installer/*`, `README.md`.
- Upstream's `cla-assistant.yml`, `notify-website.yml` and `build-msix.yml` workflows were deleted.
