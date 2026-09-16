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

using System.Security.Cryptography;
using Lockwell.Mobile.Services;

namespace Lockwell.Mobile.Views;

public partial class UnlockPage : ContentPage
{
    private readonly VaultLibrary _library;
    private readonly VaultProfile _profile;
    private readonly PhoneVault _vault;
    private readonly IBiometricUnlock? _biometrics;

    private bool _promptedThisVisit;

    public UnlockPage(VaultLibrary library, VaultProfile profile)
    {
        InitializeComponent();

        _library = library;
        _profile = profile;
        _vault = new PhoneVault(library.DirectoryFor(profile));
        _biometrics = ServiceHelper.GetService<IBiometricUnlock>();

        VaultName.Text = profile.Name;
        VaultGlyph.Text = profile.Glyph;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        bool hasBiometrics = _biometrics?.IsEnabledFor(_vault.BiometricKeyFile) ?? false;
        BiometricButton.IsVisible = hasBiometrics;

        // Offer it straight away so unlocking is one tap, but only once per visit --
        // re-prompting after a cancel would trap the user in a loop.
        if (hasBiometrics && !_promptedThisVisit)
        {
            _promptedThisVisit = true;
            _ = TryBiometricAsync(silent: true);
        }
    }

    private async void OnUnlockClicked(object sender, EventArgs e)
    {
        string password = PasswordEntry.Text ?? "";
        if (password.Length == 0) return;

        ErrorLabel.IsVisible = false;
        UnlockButton.IsEnabled = false;
        UnlockButton.Text = "Unlocking...";

        try
        {
            bool ok = await Task.Run(() =>
            {
                using var secure = PhoneVault.ToSecure(password);
                return _vault.Unlock(secure);
            });

            if (!ok)
            {
                ShowError("That password did not open this vault.");
                return;
            }

            PasswordEntry.Text = "";
            await EnterVaultAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            UnlockButton.IsEnabled = true;
            UnlockButton.Text = "Unlock";
        }
    }

    private async void OnBiometricClicked(object sender, EventArgs e) =>
        await TryBiometricAsync(silent: false);

    private async Task TryBiometricAsync(bool silent)
    {
        if (_biometrics is null || !_biometrics.IsEnabledFor(_vault.BiometricKeyFile)) return;

        try
        {
            byte[]? key = await _biometrics.UnlockAsync(_profile.Id, _vault.BiometricKeyFile);
            if (key is null)
            {
                if (!silent) ShowError("Fingerprint unlock did not complete. You can use your password.");
                return;
            }

            if (_vault.UnlockWithDataKey(key))
            {
                await EnterVaultAsync();
            }
            else
            {
                CryptographicOperations.ZeroMemory(key);
                ShowError("The stored key no longer opens this vault. Use your password.");
            }
        }
        catch
        {
            if (!silent) ShowError("Fingerprint unlock is unavailable. Use your password.");
        }
    }

    private async Task EnterVaultAsync()
    {
        _library.SetActive(_profile);

        // Replace this page rather than stacking, so Back goes to the vault list and
        // never lands on an unlock screen for an already-open vault.
        await Navigation.PushAsync(new VaultPage(_library, _profile, _vault));
        Navigation.RemovePage(this);
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }

    private async void OnBackClicked(object sender, EventArgs e) => await Navigation.PopAsync();
}
