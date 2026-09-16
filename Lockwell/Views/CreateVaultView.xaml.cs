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
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lockwell.Crypto;
using Lockwell.Helpers;
using Lockwell.Services;

namespace Lockwell.Views;

public partial class CreateVaultView : UserControl
{
    private readonly VaultManager _vault;
    private readonly Action _onCreated;

    private const int MinPasswordLength = 10;

    public CreateVaultView(VaultManager vault, Action onCreated)
    {
        InitializeComponent();
        _vault = vault;
        _onCreated = onCreated;
        Loaded += (_, _) =>
        {
            Anim.PopIn(FormPanel, 320);
            Password.Focus();
        };
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        using SecureString pw = Password.SecurePassword;
        using SecureString confirm = Confirm.SecurePassword;

        UpdateStrength(pw);
        bool match = pw.Length > 0 && SecurePassword.SecureEquals(pw, confirm);
        bool longEnough = pw.Length >= MinPasswordLength;
        CreateButton.IsEnabled = match && longEnough;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void UpdateStrength(SecureString pw)
    {
        int score = SecurePassword.Read(pw, ScorePassword);
        double fraction = Math.Min(1.0, score / 5.0);
        double maxWidth = StrengthTrack.ActualWidth > 0 ? StrengthTrack.ActualWidth : 400;

        var anim = new DoubleAnimation(maxWidth * fraction, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        StrengthFill.BeginAnimation(WidthProperty, anim);

        (string label, string brushKey) = score switch
        {
            <= 1 => ("Weak", "Danger"),
            2 or 3 => ("Okay", "Warning"),
            4 => ("Strong", "Accent2"),
            _ => ("Excellent", "Accent"),
        };
        if (pw.Length == 0) { label = ""; }
        StrengthFill.Background = (Brush)FindResource(brushKey);
        StrengthLabel.Text = pw.Length is > 0 and < MinPasswordLength
            ? $"Use at least {MinPasswordLength} characters."
            : label;
    }

    private static int ScorePassword(char[] pw)
    {
        if (pw.Length == 0) return 0;
        int score = 0;
        if (pw.Length >= 10) score++;
        if (pw.Length >= 16) score++;
        if (pw.Any(char.IsUpper) && pw.Any(char.IsLower)) score++;
        if (pw.Any(char.IsDigit)) score++;
        if (pw.Any(c => !char.IsLetterOrDigit(c))) score++;
        return score;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        using SecureString pw = Password.SecurePassword;
        using SecureString confirm = Confirm.SecurePassword;

        if (!SecurePassword.SecureEquals(pw, confirm))
        {
            ShowError("Passwords do not match.");
            return;
        }

        try
        {
            CreateButton.IsEnabled = false;
            string? recoveryKey = _vault.CreateVault(pw, WantRecovery.IsChecked == true);

            if (recoveryKey is null)
            {
                _onCreated();
                return;
            }

            RecoveryKeyText.Text = recoveryKey;
            FormPanel.Visibility = Visibility.Collapsed;
            RecoveryPanel.Visibility = Visibility.Visible;
            Anim.PopIn(RecoveryPanel, 300);
        }
        catch (Exception ex)
        {
            CreateButton.IsEnabled = true;
            ShowError("Could not create vault: " + ex.Message);
        }
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.CopySecret(RecoveryKeyText.Text, 60);
        CopyKeyButton.Content = "Copied (clears in 60s)";
    }

    private void SavedKey_Changed(object sender, RoutedEventArgs e)
        => EnterButton.IsEnabled = SavedKey.IsChecked == true;

    private void Enter_Click(object sender, RoutedEventArgs e)
    {
        RecoveryKeyText.Text = "";
        _onCreated();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
