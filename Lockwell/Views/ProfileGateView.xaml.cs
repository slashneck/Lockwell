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

using System.IO;

using System.Security;

using System.Security.Cryptography;

using System.Windows;

using System.Windows.Controls;

using System.Windows.Input;

using Lockwell.Crypto;

using Lockwell.Helpers;

using Lockwell.Models;

using Lockwell.Services;

using Microsoft.Win32;



namespace Lockwell.Views;



public partial class ProfileGateView : UserControl

{

    private readonly ProfileManager _profiles;

    private readonly Action<VaultProfile> _onContinue;

    private enum PromptMode { Create, Rename }

    private PromptMode _promptMode;

    private string? _pendingImportZipPath;

    private bool _importBusy;



    public ProfileGateView(ProfileManager profiles, Action<VaultProfile> onContinue)

    {

        InitializeComponent();

        _profiles = profiles;

        _onContinue = onContinue;

        Loaded += OnLoaded;

    }



    private void OnLoaded(object sender, RoutedEventArgs e)

    {

        RefreshList();

        Anim.PopIn(Card, 320);

    }



    private void RefreshList()

    {

        var rows = _profiles.Profiles.Select(p => new ProfileRow(p, ProfileManager.ProfileHasVault(p))).ToList();

        ProfileList.ItemsSource = rows;

        if (rows.Count > 0)

            ProfileList.SelectedIndex = rows.FindIndex(r => r.Profile.Id == _profiles.ActiveProfile?.Id);

        if (ProfileList.SelectedIndex < 0 && rows.Count > 0)

            ProfileList.SelectedIndex = 0;



        DeleteProfileButton.Visibility = rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    }



    private void Continue_Click(object sender, RoutedEventArgs e)

    {

        if (ProfileList.SelectedItem is not ProfileRow row)

        {

            ShowError("Select a profile first.");

            return;

        }



        ErrorText.Visibility = Visibility.Collapsed;

        _profiles.SetActive(row.Profile);

        _onContinue(row.Profile);

    }



    private void AddProfile_Click(object sender, RoutedEventArgs e)

    {

        ShowPrompt(PromptMode.Create, "New profile", "Pick a name you will recognize on this device.", "Work vault", "Create");

    }



    private void ImportBackup_Click(object sender, RoutedEventArgs e)

    {

        if (!TryPickBackupZip(out string? zipPath))

            return;



        BeginImportFlow(zipPath!, suggestedProfileName: SuggestImportProfileName());

    }



    private void PromptImportLink_Click(object sender, RoutedEventArgs e)

    {

        HidePrompt();

        if (_pendingImportZipPath is not null)

        {

            BeginImportFlow(_pendingImportZipPath, ImportProfileName.Text.Trim());

            return;

        }



        if (!TryPickBackupZip(out string? zipPath))

            return;



        BeginImportFlow(zipPath!, suggestedProfileName: PromptInput.Text.Trim());

    }



    private void RenameProfile_Click(object sender, RoutedEventArgs e)

    {

        if (ProfileList.SelectedItem is not ProfileRow row)

        {

            ShowError("Select a profile to rename.");

            return;

        }



        ShowPrompt(PromptMode.Rename, "Rename profile", "This only changes the label on this device.", row.Profile.Name, "Save");

    }



    private void ShowPrompt(PromptMode mode, string title, string subtitle, string value, string confirmLabel)

    {

        _promptMode = mode;

        PromptTitle.Text = title;

        PromptSubtitle.Text = subtitle;

        PromptInput.Text = value;

        PromptConfirmButton.Content = confirmLabel;

        PromptImportLink.Visibility = mode == PromptMode.Create ? Visibility.Visible : Visibility.Collapsed;

        ProfilePromptOverlay.Visibility = Visibility.Visible;

        ProfilePromptOverlay.Opacity = 0;

        Anim.FadeIn(ProfilePromptOverlay, 180);

        PromptInput.Focus();

        PromptInput.SelectAll();

        ErrorText.Visibility = Visibility.Collapsed;

    }



    private void HidePrompt()

    {

        ProfilePromptOverlay.Visibility = Visibility.Collapsed;

    }



    private void PromptCancel_Click(object sender, RoutedEventArgs e) => HidePrompt();



    private void PromptInput_KeyDown(object sender, KeyEventArgs e)

    {

        if (e.Key == Key.Enter)

        {

            PromptConfirm_Click(sender, e);

            e.Handled = true;

        }

        else if (e.Key == Key.Escape)

        {

            HidePrompt();

            e.Handled = true;

        }

    }



    private void PromptConfirm_Click(object sender, RoutedEventArgs e)

    {

        string name = PromptInput.Text.Trim();

        if (name.Length == 0)

        {

            ShowError("Enter a profile name.");

            return;

        }



        try

        {

            if (_promptMode == PromptMode.Create)

            {

                var profile = _profiles.AddProfile(name);

                HidePrompt();

                RefreshList();

                ProfileList.SelectedItem = ProfileList.Items.Cast<ProfileRow>().FirstOrDefault(r => r.Profile.Id == profile.Id);

                _profiles.SetActive(profile);

                _onContinue(profile);

                return;

            }



            if (ProfileList.SelectedItem is not ProfileRow row)

            {

                ShowError("Select a profile to rename.");

                return;

            }



            _profiles.RenameProfile(row.Profile, name);

            HidePrompt();

            RefreshList();

        }

        catch (Exception ex)

        {

            ShowError(ex.Message);

        }

    }



    private void DeleteProfile_Click(object sender, RoutedEventArgs e)

    {

        if (ProfileList.SelectedItem is not ProfileRow row)

        {

            ShowError("Select a profile to delete.");

            return;

        }



        if (_profiles.Profiles.Count <= 1)

        {

            ShowError("You must keep at least one profile.");

            return;

        }



        string msg = ProfileManager.ProfileHasVault(row.Profile)

            ? $"Delete profile \"{row.Profile.Name}\" and all vault data in it? This cannot be undone."

            : $"Delete empty profile \"{row.Profile.Name}\"?";



        if (MessageBox.Show(msg, "Lockwell", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)

            return;



        if (!_profiles.DeleteProfile(row.Profile, deleteFiles: true))

        {

            ShowError("Could not delete that profile.");

            return;

        }



        RefreshList();

    }



    private bool TryPickBackupZip(out string? zipPath)

    {

        var dlg = new OpenFileDialog

        {

            Title = "Import encrypted backup",

            Filter = "Lockwell backup|*.zip|All files|*.*",

        };



        if (dlg.ShowDialog() != true)

        {

            zipPath = null;

            return false;

        }



        zipPath = dlg.FileName;

        return true;

    }



    private void BeginImportFlow(string zipPath, string suggestedProfileName)

    {

        _pendingImportZipPath = zipPath;

        ImportProfileName.Text = string.IsNullOrWhiteSpace(suggestedProfileName) ? SuggestImportProfileName() : suggestedProfileName;

        ImportPassword.Clear();

        ImportErrorText.Visibility = Visibility.Collapsed;

        ImportConfirmButton.IsEnabled = true;

        ImportConfirmButton.Content = "Import";

        ImportOverlay.Visibility = Visibility.Visible;

        ImportOverlay.Opacity = 0;

        Anim.FadeIn(ImportOverlay, 180);

        ImportProfileName.Focus();

        ImportProfileName.SelectAll();

        ErrorText.Visibility = Visibility.Collapsed;

    }



    private string SuggestImportProfileName()

    {

        if (ProfileList.SelectedItem is ProfileRow row && !row.ProfileHasVault)

            return row.Profile.Name;



        if (_profiles.Profiles.Count == 1 && !ProfileManager.ProfileHasVault(_profiles.Profiles[0]))

            return _profiles.Profiles[0].Name;



        return "Restored vault";

    }



    private VaultProfile? SelectedEmptyProfile()

    {

        if (ProfileList.SelectedItem is ProfileRow row && !row.ProfileHasVault)

            return row.Profile;



        return null;

    }



    private void HideImportOverlay()

    {

        ImportOverlay.Visibility = Visibility.Collapsed;

        _pendingImportZipPath = null;

        _importBusy = false;

    }



    private void ImportCancel_Click(object sender, RoutedEventArgs e)

    {

        if (_importBusy)

            return;



        HideImportOverlay();

    }



    private void ImportInput_KeyDown(object sender, KeyEventArgs e)

    {

        if (e.Key == Key.Enter)

        {

            ImportConfirm_Click(sender, e);

            e.Handled = true;

        }

        else if (e.Key == Key.Escape && !_importBusy)

        {

            HideImportOverlay();

            e.Handled = true;

        }

    }



    private async void ImportConfirm_Click(object sender, RoutedEventArgs e)

    {

        if (_importBusy || _pendingImportZipPath is null)

            return;



        string name = ImportProfileName.Text.Trim();

        SecureString password = ImportPassword.SecurePassword;



        if (name.Length == 0)

        {

            password.Dispose();

            ShowImportError("Enter a profile name.");

            return;

        }



        if (password.Length == 0)

        {

            password.Dispose();

            ShowImportError("Enter the backup master password.");

            return;

        }



        _importBusy = true;

        ImportConfirmButton.IsEnabled = false;

        ImportConfirmButton.Content = "Importing...";

        ImportErrorText.Visibility = Visibility.Collapsed;



        string zipPath = _pendingImportZipPath;

        VaultProfile? reuse = SelectedEmptyProfile();



        try

        {

            var profile = await Task.Run(() =>

            {

                var target = _profiles.PrepareProfileForBackupImport(name, reuse);

                VaultBackupService.RestoreToVaultDirectory(zipPath, target.VaultDir, password);

                return target;

            });



            HideImportOverlay();

            RefreshList();

            ProfileList.SelectedItem = ProfileList.Items.Cast<ProfileRow>()

                .FirstOrDefault(r => r.Profile.Id == profile.Id);

            _profiles.SetActive(profile);

            _onContinue(profile);

        }

        catch (CryptographicException)

        {

            ShowImportError("Wrong password or backup file is damaged.");

        }

        catch (Exception ex)

        {

            ShowImportError(ex.Message);

        }

        finally

        {

            password.Dispose();

            _importBusy = false;

            ImportConfirmButton.IsEnabled = true;

            ImportConfirmButton.Content = "Import";

        }

    }



    private void ShowError(string message)

    {

        ErrorText.Text = message;

        ErrorText.Visibility = Visibility.Visible;

    }



    private void ShowImportError(string message)

    {

        ImportErrorText.Text = message;

        ImportErrorText.Visibility = Visibility.Visible;

    }



    private sealed class ProfileRow

    {

        public VaultProfile Profile { get; }

        public bool ProfileHasVault { get; }

        public string Name => Profile.Name;

        public string StatusLabel { get; }



        public ProfileRow(VaultProfile profile, bool hasVault) =>

            (Profile, ProfileHasVault, StatusLabel) = (profile, hasVault, hasVault ? "Vault ready" : "Not set up yet");

    }

}


