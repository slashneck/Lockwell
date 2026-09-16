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

/// <summary>
/// The vault list: the app's front door.
///
/// Several vaults can live on one phone, each with its own password and its own key.
/// They are genuinely independent -- opening one reveals nothing about another -- which
/// is what makes keeping "work" and "personal" on the same phone reasonable.
/// </summary>
public partial class VaultsPage : ContentPage
{
    private readonly VaultLibrary _library = new();

    public VaultsPage()
    {
        InitializeComponent();

        // A crash can leave decrypted scratch files behind, so clear them on launch
        // rather than trusting the previous run to have tidied up.
        MobileVaultPaths.PurgeTemp();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
    }

    private void Refresh()
    {
        VaultList.Clear();

        var vaults = _library.Vaults.OrderByDescending(v => v.LastOpenedUtc).ToList();
        EmptyCard.IsVisible = vaults.Count == 0;

        foreach (var vault in vaults)
            VaultList.Add(BuildVaultCard(vault));

        StorageUsed.Text = Format.Bytes(MobileVaultPaths.TotalBytesUsed());
    }

    private View BuildVaultCard(VaultProfile profile)
    {
        var card = new Border
        {
            BackgroundColor = Theme.Color("Bg2"),
            Stroke = Theme.Color("Stroke"),
            StrokeThickness = 1,
            Padding = new Thickness(16, 14),
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };

        grid.Add(new Label
        {
            Text = profile.Glyph,
            FontSize = 26,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 14, 0),
        }, 0);

        bool exists = File.Exists(Path.Combine(_library.DirectoryFor(profile), "vault.lwv"));

        var labels = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        labels.Add(new Label
        {
            Text = profile.Name,
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            TextColor = Theme.Color("TextColor"),
        });
        labels.Add(new Label
        {
            Text = exists
                ? $"{Format.Bytes(_library.SizeOf(profile))}  ·  locked"
                : "Not set up yet",
            FontSize = 11,
            TextColor = Theme.Color("Muted"),
        });
        grid.Add(labels, 1);

        grid.Add(new Label
        {
            Text = "›",
            FontSize = 24,
            TextColor = Theme.Color("Muted"),
            VerticalOptions = LayoutOptions.Center,
        }, 2);

        card.Content = grid;

        var open = new TapGestureRecognizer();
        open.Tapped += async (_, _) => await OpenVaultAsync(profile);
        card.GestureRecognizers.Add(open);

        // Long press for manage, so the common action stays a single tap.
        var manage = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        manage.Tapped += async (_, _) => await ManageVaultAsync(profile);
        card.GestureRecognizers.Add(manage);

        return card;
    }

    private async Task OpenVaultAsync(VaultProfile profile)
    {
        _library.SetActive(profile);
        await Navigation.PushAsync(new UnlockPage(_library, profile));
    }

    private async Task ManageVaultAsync(VaultProfile profile)
    {
        string? action = await this.ShowSheet(
            profile.Name, "Cancel", "Delete vault", "Rename", "Open");

        switch (action)
        {
            case "Open":
                await OpenVaultAsync(profile);
                break;

            case "Rename":
                string? name = await this.ShowPrompt(
                    "Rename vault", "What should this vault be called?",
                    initialValue: profile.Name, maxLength: 40);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _library.Rename(profile.Id, name.Trim());
                    Refresh();
                }
                break;

            case "Delete vault":
                await ConfirmDeleteAsync(profile);
                break;
        }
    }

    private async Task ConfirmDeleteAsync(VaultProfile profile)
    {
        bool sure = await this.ShowConfirm(
            $"Delete \"{profile.Name}\"?",
            "Everything in this vault is deleted permanently. Without the password it was " +
            "unreadable anyway, so there is nothing to recover afterwards.",
            "Delete", "Cancel");

        if (!sure) return;

        // A second, typed confirmation. Losing a vault is not something to do by
        // mis-tapping a dialog.
        string? typed = await this.ShowPrompt(
            "Type the vault name",
            $"To confirm, type: {profile.Name}",
            accept: "Delete forever", cancel: "Cancel", maxLength: 60);

        if (typed?.Trim() != profile.Name)
        {
            if (typed is not null)
                await this.ShowAlert("Not deleted", "That did not match, so nothing was changed.", "OK");
            return;
        }

        _library.Delete(profile.Id);
        Refresh();
    }

    private async void OnCreateVaultClicked(object sender, EventArgs e) =>
        await Navigation.PushAsync(new CreateVaultPage(_library));
}
