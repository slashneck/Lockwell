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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lockwell.Services;

namespace Lockwell.Views;

/// <summary>
/// The update section of Settings, and the quiet background check behind it.
///
/// Two rules shape all of this. Nothing is installed without the user pressing something,
/// and nothing about the vault is involved at any point -- the update path reads and
/// writes the program directory and a temp folder, and has no idea the vault exists.
/// </summary>
public partial class VaultShellView
{
    private readonly UpdateService _updates = new();
    private UpdateService.CheckResult? _pendingUpdate;
    private CancellationTokenSource? _updateCts;

    /// <summary>
    /// The once-a-day check, run shortly after unlock.
    ///
    /// It never interrupts. If something is found, the sidebar grows a quiet marker and
    /// Settings has the detail; there is no dialog in front of someone who just opened
    /// their vault to do something else.
    /// </summary>
    private async void MaybeCheckForUpdatesAsync()
    {
        if (!_settings.CheckForUpdates) return;
        if (!Lockwell.Update.ReleaseTrust.IsConfigured) return;

        DateTimeOffset? last = _settings.LastUpdateCheckUtc;
        if (last is not null && DateTimeOffset.UtcNow - last.Value < TimeSpan.FromHours(20)) return;

        try
        {
            // Let the app settle before doing anything on the network. Unlocking already
            // costs a few seconds of Argon2; competing with that helps nobody.
            await Task.Delay(TimeSpan.FromSeconds(6));

            var result = await _updates.CheckAsync();
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            _settings.Save();

            if (result.Outcome != UpdateService.Outcome.UpdateAvailable) return;
            if (string.Equals(result.Manifest?.Version, _settings.SkippedUpdateVersion, StringComparison.Ordinal))
                return;

            _pendingUpdate = result;
            ShowUpdateMarker(result.Manifest!.Version);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-check is not an error.
        }
        catch (Exception ex)
        {
            // An update check must never be able to take the app down. It is the one
            // feature that depends on a network it does not control.
            CrashLog.Write(ex);
        }
    }

    private void ShowUpdateMarker(string version)
    {
        UpdateBadge.Visibility = Visibility.Visible;
        UpdateBadgeText.Text = version;
        UpdateBadge.ToolTip = $"Lockwell {version} is available. Open Settings to install it.";
    }

    /// <summary>The Updates block inside the Settings overlay.</summary>
    private void AddUpdateSettings()
    {
        OverlayBody.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("Stroke"),
            Margin = new Thickness(0, 18, 0, 14),
        });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Updates",
            Style = (Style)FindResource("H2"),
        });

        OverlayBody.Children.Add(new TextBlock
        {
            Text = $"You have Lockwell {UpdateService.CurrentVersion.ToString(3)}.",
            Style = (Style)FindResource("Body"),
            Margin = new Thickness(0, 6, 0, 10),
        });

        if (!Lockwell.Update.ReleaseTrust.IsConfigured)
        {
            OverlayBody.Children.Add(new TextBlock
            {
                Text = "This build has no update signing key, so it cannot tell a genuine "
                     + "release from a forged one. Updating is disabled. Download new "
                     + "versions from the releases page instead.",
                Style = (Style)FindResource("Caption"),
                Foreground = (Brush)FindResource("Warning"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
            OverlayBody.Children.Add(BuildReleasesPageLink());
            return;
        }

        var autoCheck = AddToggle("Check for updates automatically", _settings.CheckForUpdates);
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Once a day, Lockwell asks GitHub whether a newer release exists. It sends "
                 + "no identifier, no version number and nothing about your vault, but it is "
                 + "still a request, so GitHub sees your address and roughly when you used "
                 + "the app. Nothing is ever installed without you choosing to.",
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var status = new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Visibility = Visibility.Collapsed,
        };

        var installButton = new Button
        {
            Style = (Style)FindResource("AccentButton"),
            Height = 40,
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
        };

        var checkButton = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Check now",
            Height = 40,
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
        };

        void ShowStatus(string text, string brushKey = "Muted")
        {
            status.Text = text;
            status.Foreground = (Brush)FindResource(brushKey);
            status.Visibility = Visibility.Visible;
        }

        void OfferInstall(UpdateService.CheckResult result)
        {
            _pendingUpdate = result;
            installButton.Content = $"Download and install {result.Manifest!.Version}";
            installButton.Visibility = Visibility.Visible;

            string notes = string.IsNullOrWhiteSpace(result.Manifest.Notes)
                ? ""
                : "\n\n" + result.Manifest.Notes.Trim();
            ShowStatus($"Lockwell {result.Manifest.Version} is available.{notes}", "Accent");
        }

        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            installButton.Visibility = Visibility.Collapsed;
            ShowStatus("Checking…");

            try
            {
                _updateCts?.Cancel();
                _updateCts = new CancellationTokenSource();

                var result = await _updates.CheckAsync(_updateCts.Token);
                _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
                _settings.Save();

                switch (result.Outcome)
                {
                    case UpdateService.Outcome.UpdateAvailable:
                        OfferInstall(result);
                        break;

                    case UpdateService.Outcome.NotTrusted:
                        // Worth being loud about. This is not "the network is flaky", it is
                        // "something is publishing releases that are not ours".
                        ShowStatus(result.Describe(), "Danger");
                        break;

                    case UpdateService.Outcome.ManualInstallRequired:
                        ShowStatus(result.Describe(), "Warning");
                        break;

                    default:
                        ShowStatus(result.Describe());
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Check cancelled.");
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                ShowStatus("Could not check for updates.", "Danger");
            }
            finally
            {
                checkButton.IsEnabled = true;
            }
        };

        installButton.Click += async (_, _) =>
        {
            if (_pendingUpdate is null) return;

            installButton.IsEnabled = false;
            checkButton.IsEnabled = false;

            string? failure = null;
            try
            {
                _updateCts?.Cancel();
                _updateCts = new CancellationTokenSource();

                var progress = new Progress<double>(p =>
                    ShowStatus($"Downloading… {p:0}%", "Accent"));

                string? package = await _updates.DownloadVerifiedPackageAsync(
                    _pendingUpdate, progress, reason => failure = reason, _updateCts.Token);

                if (package is null)
                {
                    ShowStatus(failure ?? "The update could not be downloaded.", "Danger");
                    return;
                }

                ShowStatus("Verified. Starting the installer…", "Accent");

                if (!UpdateService.LaunchInstaller(package, _pendingUpdate, reason => failure = reason))
                {
                    ShowStatus(failure ?? "The update was not started.", "Danger");
                    return;
                }

                // The installer waits for this process to exit before touching anything.
                // Lock on the way out so a vault is never left unlocked behind an
                // installer that is about to restart the app.
                CloseOverlay();
                _onLock();
                Application.Current.Shutdown();
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Download cancelled.");
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                ShowStatus("The update could not be installed.", "Danger");
            }
            finally
            {
                installButton.IsEnabled = true;
                checkButton.IsEnabled = true;
            }
        };

        OverlayBody.Children.Add(status);
        OverlayBody.Children.Add(installButton);
        OverlayBody.Children.Add(checkButton);
        OverlayBody.Children.Add(BuildReleasesPageLink());

        // If the background check already found something, say so on open rather than
        // making the user press Check now to be told what the badge already implied.
        if (_pendingUpdate?.Manifest is not null) OfferInstall(_pendingUpdate);

        _updateSettingsCommit = () =>
        {
            _settings.CheckForUpdates = autoCheck.IsChecked == true;
        };
    }

    private Action? _updateSettingsCommit;

    private void GetAndroidApp_Click(object sender, RoutedEventArgs e) =>
        OpenInBrowser(UpdateService.ReleasesPageUrl);

    /// <summary>
    /// The licence notice, at the bottom of Settings.
    ///
    /// Lockwell is GPL software, and the point of that is only real if the people running
    /// it know, so it says so somewhere they will actually look rather than only in a file
    /// next to the executable.
    /// </summary>
    private void AddAboutSettings()
    {
        OverlayBody.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("Stroke"),
            Margin = new Thickness(0, 18, 0, 14),
        });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "About",
            Style = (Style)FindResource("H2"),
        });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Lockwell is free software under the GNU General Public License, version 3 "
                 + "or later. You can read every line of it, build it yourself, change it, "
                 + "and share it. The full licence is in LICENSE.txt next to the app.",
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 8),
        });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Copyright 2026 Lockwell. Comes with absolutely no warranty.",
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        var source = new Button
        {
            Style = (Style)FindResource("LinkButton"),
            Content = "View the source code",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        source.Click += (_, _) => OpenInBrowser(UpdateService.SourceCodeUrl);
        OverlayBody.Children.Add(source);
    }

    private UIElement BuildReleasesPageLink()
    {
        var link = new Button
        {
            Style = (Style)FindResource("LinkButton"),
            Content = "Open the releases page",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        link.Click += (_, _) => OpenInBrowser(UpdateService.ReleasesPageUrl);
        return link;
    }

    /// <summary>
    /// Hand a URL to the default browser.
    ///
    /// <c>UseShellExecute</c> with an https URL only. Never a path, never anything the user
    /// did not choose from a fixed list in this app, because ShellExecute will happily run
    /// whatever it is handed.
    /// </summary>
    internal static void OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.FileNotFoundException)
        {
            // No default browser, or the user declined. Nothing useful to do about it.
        }
    }
}
