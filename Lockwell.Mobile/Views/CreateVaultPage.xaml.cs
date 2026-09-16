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

using Lockwell.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace Lockwell.Mobile.Views;

public partial class CreateVaultPage : ContentPage
{
    private static readonly string[] Glyphs =
    {
        "\U0001F512", "\U0001F5BC", "\U0001F4BC", "\U0001F3E0",
        "\U0001F3AE", "\U0001F3B5", "\U0001F4C4", "\U0001F511",
    };

    private readonly VaultLibrary _library;
    private readonly IBiometricUnlock? _biometrics;
    private string _glyph = Glyphs[0];

    public CreateVaultPage(VaultLibrary library)
    {
        InitializeComponent();
        _library = library;
        _biometrics = ServiceHelper.GetService<IBiometricUnlock>();
        BuildGlyphRow();
    }

    private void BuildGlyphRow()
    {
        foreach (string glyph in Glyphs)
        {
            string captured = glyph;
            var border = new Border
            {
                BackgroundColor = Theme.Color(glyph == _glyph ? "Bg3" : "Bg2"),
                Stroke = Theme.Color(glyph == _glyph ? "Accent" : "Stroke"),
                StrokeThickness = 1,
                WidthRequest = 52,
                HeightRequest = 52,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Content = new Label
                {
                    Text = glyph,
                    FontSize = 22,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                },
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _glyph = captured;
                GlyphRow.Clear();
                BuildGlyphRow();
            };
            border.GestureRecognizers.Add(tap);

            GlyphRow.Add(border);
        }
    }

    private void OnPasswordChanged(object sender, TextChangedEventArgs e)
    {
        string value = e.NewTextValue ?? "";
        (string text, string colour) = value.Length switch
        {
            0 => ("A few unrelated words is stronger than symbols.", "Muted"),
            < 10 => ($"{10 - value.Length} more character(s) needed.", "Warning"),
            < 16 => ("Reasonable. Longer beats more symbols.", "Warning"),
            _ => ("Strong.", "Accent"),
        };

        StrengthLabel.Text = text;
        StrengthLabel.TextColor = Theme.Color(colour);
    }

    private async void OnCreateClicked(object sender, EventArgs e)
    {
        string name = (NameEntry.Text ?? "").Trim();
        string password = PasswordEntry.Text ?? "";
        string confirm = ConfirmEntry.Text ?? "";

        if (name.Length == 0)
        {
            await this.ShowAlert("Name it", "Give the vault a name so you can tell it apart.", "OK");
            return;
        }

        if (password.Length < 10)
        {
            await this.ShowAlert("Password too short",
                "Use at least 10 characters. A few unrelated words works well and is easier to remember.", "OK");
            return;
        }

        if (password != confirm)
        {
            await this.ShowAlert("Passwords do not match", "Type the same password in both fields.", "OK");
            return;
        }

        VaultProfile? profile = null;
        try
        {
            CreateButton.IsEnabled = false;
            CreateButton.Text = "Creating...";

            profile = _library.Create(name, _glyph);
            var vault = new PhoneVault(_library.DirectoryFor(profile));

            // Argon2id at 256 MiB takes a moment on a phone, so keep it off the UI thread.
            await Task.Run(() =>
            {
                using var secure = PhoneVault.ToSecure(password);
                vault.Create(secure);
            });

            await OfferBiometricsAsync(profile, vault);

            // Straight into the vault rather than back to a list. Push first, then drop
            // this page: popping first detaches it, and the push that follows goes
            // nowhere, leaving the user staring at the vault list they just left.
            await Navigation.PushAsync(new VaultPage(_library, profile, vault));
            Navigation.RemovePage(this);
        }
        catch (Exception ex)
        {
            // A half-created vault would sit in the list looking real, so remove it.
            if (profile is not null) _library.Delete(profile.Id);
            await this.ShowAlert("Could not create the vault", ex.Message, "OK");
        }
        finally
        {
            CreateButton.IsEnabled = true;
            CreateButton.Text = "Create vault";
        }
    }

    private async Task OfferBiometricsAsync(VaultProfile profile, PhoneVault vault)
    {
        if (_biometrics is null) return;

        if (_biometrics.Check() != BiometricAvailability.Available) return;

        bool wants = await this.ShowConfirm(
            "Unlock with fingerprint?",
            "Your password still protects this vault. A fingerprint just unlocks the key held " +
            "in this phone's secure hardware, so you do not have to type it every time.",
            "Set it up", "Not now");

        if (!wants) return;

        try
        {
            await _biometrics.EnableAsync(
                profile.Id, vault.BiometricKeyFile, vault.ExportDataKeyForBiometricWrap());
        }
        catch
        {
            // Not being able to enable a convenience feature is not worth blocking on.
        }
    }
}
