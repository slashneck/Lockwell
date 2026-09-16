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

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using Lockwell.Installer.Services;

namespace Lockwell.Installer;

internal sealed class InstallerViewModel : INotifyPropertyChanged
{
    private int _stepIndex;
    private bool _termsAccepted;
    private string _installPath = InstallPaths.ElevatedDefaultInstallDirectory;
    private bool _createDesktopShortcut = true;
    private bool _createStartMenuShortcut = false;
    private bool _isBusy;
    private bool _installComplete;
    private bool _installFailed;
    private string _statusText = string.Empty;
    private double _progress;
    private string _releaseSummary = string.Empty;
    private string? _pendingDownloadUrl;
    private Version? _pendingVersion;
    private string? _pendingTag;

    private bool _isPatchMode;
    private string? _detectedInstallPath;
    private string? _patchedInstallDir;
    private UpdateFlow.UpdateRequest _patchRequest = new(null, null, null, null, null, false);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StepIndicator =>
        StepIndex == 0
            ? "Choose an option"
            : IsPatchMode && StepIndex == 5
                ? "Update"
                : $"Step {StepIndex} of 4";

    public string CurrentStepTitle => StepTitles[StepIndex];

    public bool ShowStepProgress => StepIndex is >= 1 and <= 4;

    /// <summary>One entry in the side rail. Step 0 and the working step are not listed,
    /// because neither is something the user can navigate back to.</summary>
    public sealed record StepRailEntry(int Number, string Label);

    public IReadOnlyList<StepRailEntry> StepRail { get; } =
    [
        new(1, "Terms"),
        new(2, "Install location"),
        new(3, "Shortcuts"),
        new(4, "Review"),
    ];

    public IReadOnlyList<string> StepTitles { get; } =
    [
        "Get started",
        "Terms",
        "Install location",
        "Shortcuts",
        "Review",
        "Working"
    ];

    public bool IsPatchMode
    {
        get => _isPatchMode;
        private set
        {
            if (_isPatchMode == value)
            {
                return;
            }

            _isPatchMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressTitle));
            OnPropertyChanged(nameof(ProgressSubtitle));
            NotifyAllNavigation();
        }
    }

    public bool HasExistingInstall => !string.IsNullOrWhiteSpace(_detectedInstallPath);

    public string DetectedInstallSummary =>
        HasExistingInstall
            ? $"Detected install: {_detectedInstallPath}"
            : "No existing install detected on this PC.";

    public string ProgressTitle =>
        InstallComplete
            ? IsPatchMode ? "Update complete" : "Finished"
            : IsPatchMode ? "Updating" : "Installing";

    public string ProgressSubtitle =>
        InstallComplete
            ? "You can close setup, or start Lockwell now."
            : IsPatchMode
                ? "Verifying the signed release, then updating your install…"
                : "Verifying the signed release, then installing…";

    public int StepIndex
    {
        get => _stepIndex;
        set
        {
            if (_stepIndex == value)
            {
                return;
            }

            _stepIndex = value;
            NotifyAllNavigation();
        }
    }

    public bool TermsAccepted
    {
        get => _termsAccepted;
        set
        {
            if (_termsAccepted == value)
            {
                return;
            }

            _termsAccepted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanGoNext));
        }
    }

    public string InstallPath
    {
        get => _installPath;
        set
        {
            if (_installPath == value)
            {
                return;
            }

            _installPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PathExistsWarning));
            OnPropertyChanged(nameof(ReviewInstallPath));
        }
    }

    public bool CreateDesktopShortcut
    {
        get => _createDesktopShortcut;
        set
        {
            if (_createDesktopShortcut == value)
            {
                return;
            }

            _createDesktopShortcut = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReviewDesktopShortcut));
        }
    }

    public bool CreateStartMenuShortcut
    {
        get => _createStartMenuShortcut;
        set
        {
            if (_createStartMenuShortcut == value)
            {
                return;
            }

            _createStartMenuShortcut = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReviewStartMenuShortcut));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            NotifyAllNavigation();
        }
    }

    public bool InstallComplete
    {
        get => _installComplete;
        private set
        {
            if (_installComplete == value)
            {
                return;
            }

            _installComplete = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressTitle));
            OnPropertyChanged(nameof(ProgressSubtitle));
            NotifyAllNavigation();
        }
    }

    public bool InstallFailed
    {
        get => _installFailed;
        private set
        {
            if (_installFailed == value)
            {
                return;
            }

            _installFailed = value;
            OnPropertyChanged();
            NotifyAllNavigation();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (Math.Abs(_progress - value) < 0.01)
            {
                return;
            }

            _progress = value;
            OnPropertyChanged();
        }
    }

    public string ReleaseSummary
    {
        get => _releaseSummary;
        private set
        {
            if (_releaseSummary == value)
            {
                return;
            }

            _releaseSummary = value;
            OnPropertyChanged();
        }
    }

    public string TermsText => InstallerTerms.Text;

    public bool PathExistsWarning =>
        Directory.Exists(InstallPath) &&
        File.Exists(Path.Combine(InstallPath, InstallPaths.ExecutableFileName));

    public string ReviewInstallPath => InstallPath;
    public string ReviewDesktopShortcut => CreateDesktopShortcut ? "Yes" : "No";
    public string ReviewStartMenuShortcut => CreateStartMenuShortcut ? "Yes" : "No";
    public string ReviewRelease => _pendingTag is null ? ReleaseSummary : $"{_pendingTag} ({_pendingVersion})";

    public bool CanGoBack => !IsBusy && !InstallComplete && StepIndex is > 0 and < 5;
    public bool CanGoNext => !IsBusy && StepIndex is >= 1 and < 4 && (StepIndex != 1 || TermsAccepted);
    public bool ShowNextButton => !IsBusy && StepIndex is >= 1 and <= 3;
    public bool ShowInstallButton => StepIndex == 4 && !IsBusy && !InstallComplete && !IsPatchMode;
    public bool ShowFinishButton => StepIndex == 5 && InstallComplete;
    public bool ShowRunButton => StepIndex == 5 && InstallComplete;
    public bool ShowRetryButton => StepIndex == 5 && InstallFailed && !IsBusy;
    public bool ShowCancelButton => !IsBusy && !InstallComplete;
    public string NextButtonLabel => StepIndex == 4 ? "Install" : "Next";

    public async Task InitializeAsync()
    {
        _detectedInstallPath = UpdateFlow.ResolveInstallDirectory();
        OnPropertyChanged(nameof(HasExistingInstall));
        OnPropertyChanged(nameof(DetectedInstallSummary));

        try
        {
            var release = await InstallerEngine.FetchLatestReleaseAsync().ConfigureAwait(true);
            if (release is null)
            {
                ReleaseSummary = "Latest release";
                return;
            }

            _pendingDownloadUrl = release.DownloadUrl;
            _pendingVersion = release.LatestVersion;
            _pendingTag = release.TagName;
            ReleaseSummary = $"{release.TagName} · v{release.LatestVersion}";
            OnPropertyChanged(nameof(ReviewRelease));
        }
        catch
        {
            ReleaseSummary = "Latest release";
        }
    }

    public void ChooseInstall()
    {
        if (IsBusy)
        {
            return;
        }

        IsPatchMode = false;
        InstallFailed = false;
        InstallComplete = false;
        StepIndex = 1;
    }

    public void ChoosePatch()
    {
        if (IsBusy)
        {
            return;
        }

        if (!HasExistingInstall)
        {
            MessageBox.Show(
                "No existing Lockwell install was found on this PC. Use Install to set up the app first.",
                "Lockwell Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        IsPatchMode = true;
        _patchRequest = new UpdateFlow.UpdateRequest(null, null, null, null, null, NoRestart: true);
        InstallFailed = false;
        InstallComplete = false;
        StepIndex = 5;
        _ = RunPatchAsync();
    }

    public void StartPatchFromArgs(UpdateFlow.UpdateRequest request)
    {
        if (!HasExistingInstall && string.IsNullOrWhiteSpace(UpdateFlow.ResolveInstallDirectory()))
        {
            MessageBox.Show(
                "No existing Lockwell install was found on this PC.",
                "Lockwell Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _patchRequest = request;
        IsPatchMode = true;
        InstallFailed = false;
        InstallComplete = false;
        StepIndex = 5;
        _ = RunPatchAsync();
    }

    public void BrowseInstallPath()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose install folder",
            InitialDirectory = Directory.Exists(InstallPath) ? InstallPath : InstallPaths.ElevatedDefaultInstallDirectory,
            FolderName = InstallPaths.InstallFolderName
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            InstallPath = dlg.FolderName;
        }
    }

    public void StartInstall()
    {
        if (StepIndex != 4 || IsBusy)
        {
            return;
        }

        StepIndex = 5;
        _ = RunInstallAsync();
    }

    public void GoNext()
    {
        if (CanGoNext)
        {
            StepIndex++;
        }
    }

    public async Task RetryInstallAsync()
    {
        InstallFailed = false;
        InstallComplete = false;
        if (IsPatchMode)
        {
            await RunPatchAsync().ConfigureAwait(true);
        }
        else
        {
            await RunInstallAsync().ConfigureAwait(true);
        }
    }

    public void GoBack()
    {
        if (CanGoBack)
        {
            StepIndex--;
        }
    }

    public async Task RunInstallAsync()
    {
        if (IsBusy)
        {
            return;
        }

        InstallFailed = false;
        InstallComplete = false;
        IsBusy = true;
        Progress = 0;

        try
        {
            if (string.IsNullOrWhiteSpace(_pendingDownloadUrl))
            {
                StatusText = "Checking for the latest release…";
                var release = await InstallerEngine.FetchLatestReleaseAsync().ConfigureAwait(true);
                if (release is null)
                {
                    throw new InvalidOperationException("Could not reach the update server or no release package was found.");
                }

                _pendingDownloadUrl = release.DownloadUrl;
                _pendingVersion = release.LatestVersion;
                _pendingTag = release.TagName;
                OnPropertyChanged(nameof(ReviewRelease));
            }

            var installDir = Path.GetFullPath(InstallPath.Trim());
            Directory.CreateDirectory(installDir);

            StatusText = "Downloading Lockwell…";
            var zip = await InstallerEngine.DownloadReleaseZipAsync(
                _pendingDownloadUrl!,
                new Progress<double>(p => Progress = p * 0.72)).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(zip) || !File.Exists(zip))
            {
                throw new InvalidOperationException("Download failed.");
            }

            StatusText = "Installing files…";
            Progress = 78;
            if (!GitHubReleaseClient.InstallReleaseZipToDirectory(zip, installDir))
            {
                throw new InvalidOperationException("Could not extract the release package.");
            }

            StatusText = "Creating shortcuts…";
            Progress = 88;
            var plan = new InstallerEngine.InstallPlan(
                installDir,
                CreateDesktopShortcut,
                CreateStartMenuShortcut,
                _pendingTag ?? "latest",
                _pendingVersion ?? new Version(1, 0, 0),
                _pendingDownloadUrl!);
            InstallerEngine.ApplyShortcuts(plan);

            StatusText = "Registering with Windows…";
            Progress = 94;
            var uninstallExe = InstallRegistryService.CopyUninstallerToInstallDir(installDir);
            InstallRegistryService.RegisterInstall(
                installDir,
                _pendingVersion ?? GitHubReleaseClient.ReadAppVersion() ?? new Version(1, 0, 0),
                uninstallExe);

            try
            {
                File.Delete(zip);
            }
            catch
            {
                // ignore temp cleanup
            }

            Progress = 100;
            StatusText = "Installation finished.";
            InstallComplete = true;
        }
        catch (Exception ex)
        {
            InstallFailed = true;
            StatusText = ex.Message;
            Progress = 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Finish(Window window) => window.Close();

    public void RunProgram(Window window)
    {
        var installDir = IsPatchMode
            ? _patchedInstallDir ?? _detectedInstallPath ?? InstallPath
            : InstallPath;
        var exe = Path.Combine(installDir, InstallPaths.ExecutableFileName);
        if (File.Exists(exe))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? installDir
            });
        }

        window.Close();
    }

    public async Task RunPatchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        InstallFailed = false;
        InstallComplete = false;
        IsBusy = true;
        Progress = 0;

        try
        {
            var progress = new Progress<(string Status, double Progress)>(report =>
            {
                StatusText = report.Status;
                Progress = Math.Clamp(report.Progress * 100, 0, 100);
            });

            await UpdateFlow.ExecuteAsync(_patchRequest, progress, CancellationToken.None).ConfigureAwait(true);
            _patchedInstallDir = UpdateFlow.ResolveInstallDirectory();
            Progress = 100;
            StatusText = IsPatchMode ? "Update finished." : StatusText;
            InstallComplete = true;
        }
        catch (Exception ex)
        {
            InstallFailed = true;
            StatusText = ex.Message;
            Progress = 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyAllNavigation()
    {
        OnPropertyChanged(nameof(StepIndex));
        OnPropertyChanged(nameof(StepIndicator));
        OnPropertyChanged(nameof(CurrentStepTitle));
        OnPropertyChanged(nameof(ShowStepProgress));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(ShowNextButton));
        OnPropertyChanged(nameof(ShowInstallButton));
        OnPropertyChanged(nameof(ShowFinishButton));
        OnPropertyChanged(nameof(ShowRunButton));
        OnPropertyChanged(nameof(ShowRetryButton));
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(NextButtonLabel));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
