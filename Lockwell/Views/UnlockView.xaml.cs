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

using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Lockwell.Helpers;
using Lockwell.Services;

namespace Lockwell.Views;

public partial class UnlockView : UserControl
{
    private readonly VaultManager _vault;
    private readonly Action _onUnlocked;
    private readonly Action? _onBack;
    private readonly string? _profileName;
    private readonly PreflightScanner _scanner = new();

    private enum PendingUnlockKind { None, Password, Recovery }
    private PendingUnlockKind _pendingUnlock;
    private SecureString? _pendingPassword;
    private string _pendingRecoveryKey = "";

    public UnlockView(VaultManager vault, Action onUnlocked, string? profileName = null, Action? onBack = null)
    {
        InitializeComponent();
        _vault = vault;
        _onUnlocked = onUnlocked;
        _profileName = profileName;
        _onBack = onBack;
        UseRecoveryButton.Visibility = _vault.RecoveryEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(_profileName))
        {
            ProfileLabel.Text = _profileName;
            ProfileLabel.Visibility = Visibility.Visible;
        }
        BackButton.Visibility = _onBack is not null ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) =>
        {
            Anim.PopIn(Card, 320);
            Password.Focus();
            await RefreshUnlockRiskBannerAsync();
        };
    }

    private void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryUnlock();
    }

    private void Unlock_Click(object sender, RoutedEventArgs e) => TryUnlock();

    private async void TryUnlock()
    {
        SecureString pw = Password.SecurePassword;
        if (pw.Length == 0)
        {
            pw.Dispose();
            return;
        }

        // When the risk gate appears it takes ownership of pw until the user answers.
        if (!await ConfirmUnlockSafeAsync(PendingUnlockKind.Password, pw, ""))
            return;

        await PerformPasswordUnlockAsync(pw);
    }

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        string key = RecoveryInput.Text;
        if (string.IsNullOrWhiteSpace(key)) return;

        if (!await ConfirmUnlockSafeAsync(PendingUnlockKind.Recovery, null, key))
            return;

        await PerformRecoveryUnlockAsync(key);
    }

    private async Task<bool> ConfirmUnlockSafeAsync(PendingUnlockKind kind, SecureString? password, string recoveryKey)
    {
        var risks = await Task.Run(() => _scanner.ScanUnlockRisks());
        if (risks.Count == 0)
            return true;

        _pendingPassword?.Dispose();
        _pendingUnlock = kind;
        _pendingPassword = password;
        _pendingRecoveryKey = recoveryKey;
        UnlockRiskList.ItemsSource = risks;
        UnlockRiskAccept.IsChecked = false;
        UnlockRiskProceedButton.IsEnabled = false;
        UnlockRiskGate.Visibility = Visibility.Visible;
        UnlockRiskGate.Opacity = 0;
        Anim.FadeIn(UnlockRiskGate, 180);
        return false;
    }

    private void UnlockRiskAccept_Changed(object sender, RoutedEventArgs e) =>
        UnlockRiskProceedButton.IsEnabled = UnlockRiskAccept.IsChecked == true;

    private void UnlockRiskCancel_Click(object sender, RoutedEventArgs e)
    {
        _pendingUnlock = PendingUnlockKind.None;
        _pendingPassword?.Dispose();
        _pendingPassword = null;
        _pendingRecoveryKey = "";
        UnlockRiskGate.Visibility = Visibility.Collapsed;
    }

    private async void UnlockRiskProceed_Click(object sender, RoutedEventArgs e)
    {
        if (UnlockRiskAccept.IsChecked != true) return;

        var kind = _pendingUnlock;
        SecureString? pw = _pendingPassword;
        string key = _pendingRecoveryKey;
        UnlockRiskGate.Visibility = Visibility.Collapsed;
        _pendingUnlock = PendingUnlockKind.None;
        _pendingPassword = null;
        _pendingRecoveryKey = "";

        if (kind == PendingUnlockKind.Password && pw is not null)
            await PerformPasswordUnlockAsync(pw);
        else
        {
            pw?.Dispose();
            if (kind == PendingUnlockKind.Recovery)
                await PerformRecoveryUnlockAsync(key);
        }
    }

    private async void RescanUnlockRisk_Click(object sender, RoutedEventArgs e) =>
        await RefreshUnlockRiskBannerAsync();

    private async Task RefreshUnlockRiskBannerAsync()
    {
        var risks = await Task.Run(() => _scanner.ScanUnlockRisks());
        if (risks.Count == 0)
        {
            UnlockRiskBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var top = risks.Take(2).Select(r => r.DisplayName).ToList();
        string summary = top.Count == 1
            ? top[0]
            : $"{top[0]} and {top.Count - 1} more";
        UnlockRiskBannerText.Text =
            $"{summary}. End screen share before you unlock, or you will be asked to confirm.";
        UnlockRiskBanner.Visibility = Visibility.Visible;
    }

    private async Task PerformPasswordUnlockAsync(SecureString pw)
    {
        UnlockButton.IsEnabled = false;
        UnlockButton.Content = "Unlocking...";
        ErrorText.Visibility = Visibility.Collapsed;

        bool ok;
        try
        {
            ok = await Task.Run(() => _vault.Unlock(pw));
        }
        finally
        {
            pw.Dispose();
        }

        UnlockButton.IsEnabled = true;
        UnlockButton.Content = "Unlock";

        if (ok)
            _onUnlocked();
        else
        {
            Password.Clear();
            Password.Focus();
            ShowError("Wrong password. Try again.");
            PlayShake();
        }
    }

    private async Task PerformRecoveryUnlockAsync(string key)
    {
        RecoverButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;

        bool ok = await Task.Run(() => _vault.UnlockWithRecovery(key));

        RecoverButton.IsEnabled = true;
        RecoveryInput.Clear();

        if (ok)
            _onUnlocked();
        else
        {
            ShowError("That recovery key did not work.");
            PlayShake();
        }
    }

    private void ShowRecovery_Click(object sender, RoutedEventArgs e)
    {
        PasswordMode.Visibility = Visibility.Collapsed;
        RecoveryMode.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        RecoveryInput.Focus();
    }

    private void ShowPassword_Click(object sender, RoutedEventArgs e)
    {
        RecoveryMode.Visibility = Visibility.Collapsed;
        PasswordMode.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        Password.Focus();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _onBack?.Invoke();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void PlayShake()
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        foreach (var (t, v) in new[] { (0, 0.0), (50, -10.0), (100, 9.0), (150, -7.0), (200, 5.0), (250, 0.0) })
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
        Shake.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }
}
