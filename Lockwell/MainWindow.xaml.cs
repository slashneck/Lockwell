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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lockwell.Helpers;
using Lockwell.Models;
using Lockwell.Services;
using Lockwell.Views;
namespace Lockwell;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ProfileManager _profiles;
    private VaultManager _vault;
    private readonly AutoLockService _autoLock;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            throw;
        }

        _profiles = new ProfileManager(_settings);
        _profiles.EnsureInitialized();
        _vault = _profiles.CreateActiveVaultManager();

        _autoLock = new AutoLockService(TimeSpan.FromMinutes(Math.Max(1, _settings.AutoLockMinutes)));
        _autoLock.LockRequested += () => Dispatcher.Invoke(LockVault);

        WindowWorkAreaHelper.Attach(this);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CaptureProtection.Apply(this, _settings.ExcludeFromCapture);

        if (_settings.RunPreflight)
            ShowPreflight();
        else
            ShowGate();
    }

    // -------------------------------------------------------- navigation

    private void ShowPreflight()
    {
        SetUnlockedChrome(false);
        SetHost(new PreflightView(new PreflightScanner(), ShowGate));
    }

    private void ShowGate()
    {
        SetUnlockedChrome(false);
        SetHost(new ProfileGateView(_profiles, OnProfileSelected));
    }

    private void OnProfileSelected(VaultProfile profile)
    {
        _profiles.SetActive(profile);
        _vault = new VaultManager(profile.VaultDir);

        if (_vault.VaultExists)
            SetHost(new UnlockView(_vault, OnUnlocked, profile.Name, ShowGate));
        else
            SetHost(new CreateVaultView(_vault, OnUnlocked));
    }

    private void OnUnlocked()
    {
        SetUnlockedChrome(true);
        _autoLock.SetIdleLimit(TimeSpan.FromMinutes(Math.Max(1, _settings.AutoLockMinutes)));
        _autoLock.Start();

        if (_vault.LastIntegrityReport is { Changed: true } report)
            ShowIntegrityRecoveryNotice(report);

        SetHost(new VaultShellView(_vault, _settings, _profiles, LockVault, ApplySettings, ShowGate));
    }

    private static void ShowIntegrityRecoveryNotice(VaultIntegrityReport report)
    {
        var parts = new List<string>();
        if (report.OrphanedAttachmentsRecovered > 0)
            parts.Add($"re-indexed {report.OrphanedAttachmentsRecovered} media file(s) that were on disk but missing from the vault index");
        if (report.EntriesReassignedToMedia > 0)
            parts.Add($"moved {report.EntriesReassignedToMedia} item(s) back into Media vault");
        if (report.DanglingFolderRefsCleared > 0)
            parts.Add($"restored {report.DanglingFolderRefsCleared} file(s) whose folder link was broken");
        if (report.MediaCategoryFlagsFixed > 0)
            parts.Add("repaired the Media vault section");

        if (parts.Count == 0)
            return;

        MessageBox.Show(
            "Lockwell repaired your vault after load:\n\n" + string.Join(";\n", parts) + ".\n\nCheck Media vault. If folders still look empty, open Settings and turn off \"Hide NSFW media folders\".",
            "Lockwell",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void SetHost(UIElement view)
    {
        Host.Content = view;
        Host.Opacity = 0;
        Anim.FadeIn(Host, 260);
        if (view is FrameworkElement fe) Anim.SlideFadeIn(fe, 300, 12, 40);
    }

    private void LockVault()
    {
        _autoLock.Stop();
        if (_vault.IsUnlocked)
        {
            try { _vault.Save(); }
            catch { /* best effort before wiping memory */ }
        }
        VaultTempFiles.PurgeAll();
        _vault.Lock();
        ShowGate();
    }

    private void ApplySettings()
    {
        CaptureProtection.Apply(this, _settings.ExcludeFromCapture);
        _autoLock.SetIdleLimit(TimeSpan.FromMinutes(Math.Max(1, _settings.AutoLockMinutes)));
        _settings.Save();
    }

    private void SetUnlockedChrome(bool unlocked)
    {
        LockNowButton.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
        LockedPillText.Text = unlocked ? "Unlocked" : "Locked";
        LockedPill.Background = (System.Windows.Media.Brush)FindResource(unlocked ? "Bg3" : "Bg2");
        LockedPillText.Foreground = (System.Windows.Media.Brush)FindResource(unlocked ? "Accent" : "Muted");
    }

    // -------------------------------------------------------- window chrome

    protected override void OnClosed(EventArgs e)
    {
        if (_vault.IsUnlocked)
        {
            try { _vault.Save(); }
            catch { /* best effort */ }
        }
        base.OnClosed(e);
    }

    private void LockNowButton_Click(object sender, RoutedEventArgs e) => LockVault();
    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // The maximize/restore glyph used to be refreshed in OnDeactivated, so it only
        // caught up when the window lost focus: maximize the window and the button kept
        // the maximize icon until you clicked another app. It belongs here, where the
        // state actually changes.
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore down" : "Maximize";

        // A borderless window keeps its rounded corners and hairline border when it is
        // maximized, which leaves a rounded bite out of each screen corner and a line
        // down the edge of the display. Square it off while it fills the screen.
        WindowShell.CornerRadius = WindowState == WindowState.Maximized
            ? new CornerRadius(0)
            : new CornerRadius(12);
        WindowShell.BorderThickness = WindowState == WindowState.Maximized
            ? new Thickness(0)
            : new Thickness(1);

        if (WindowState == WindowState.Minimized && _vault.IsUnlocked && _settings.LockOnMinimize)
            LockVault();
    }
}
