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
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lockwell.Crypto;
using Lockwell.Services;

namespace Lockwell.Views;

/// <summary>
/// Settings as a page rather than one long dialog with a Save button at the bottom.
///
/// Changes apply as they are made, so there is no moment where a setting looks changed but
/// is not. The exception is the master password, which has its own button and its own
/// confirmation, because that one should never happen by accident.
/// </summary>
public partial class VaultShellView
{
    private enum SettingsSection { General, Security, Backup, Updates, About }

    private bool _settingsMode;
    private SettingsSection _settingsSection = SettingsSection.General;

    /// <summary>Where the shared form helpers add controls: the page, or a dialog.</summary>
    private Panel? _formHost;

    private Panel FormHost => _formHost ?? OverlayBody;

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(_settingsSection);

    private void ShowSettings(SettingsSection section)
    {
        HideAllPanels();
        _settingsMode = true;
        _settingsSection = section;
        SettingsPanel.Visibility = Visibility.Visible;
        BuildSettingsNav();
        RenderSettingsSection();
    }

    private void BuildSettingsNav()
    {
        SettingsNav.Children.Clear();

        void Add(string label, SettingsSection section)
        {
            var button = new ToggleButton
            {
                Style = (Style)FindResource("NavButton"),
                Content = label,
                IsChecked = _settingsSection == section,
            };
            button.Click += (_, _) => ShowSettings(section);
            SettingsNav.Children.Add(button);
        }

        Add("General", SettingsSection.General);
        Add("Security", SettingsSection.Security);
        Add("Backup and storage", SettingsSection.Backup);
        Add("Updates", SettingsSection.Updates);
        Add("About", SettingsSection.About);
    }

    private void RenderSettingsSection()
    {
        SettingsBody.Children.Clear();
        SettingsScroller.ScrollToTop();
        _formHost = SettingsBody;

        try
        {
            switch (_settingsSection)
            {
                case SettingsSection.General: RenderGeneralSettings(); break;
                case SettingsSection.Security: RenderSecuritySettings(); break;
                case SettingsSection.Backup: RenderBackupSettings(); break;
                case SettingsSection.Updates: AddUpdateSettings(); break;
                case SettingsSection.About: AddAboutSettings(); break;
            }
        }
        finally
        {
            _formHost = null;
        }
    }

    // ---------------------------------------------------------------- general

    private void RenderGeneralSettings()
    {
        AddSettingsHeading("General", first: true);

        AddSettingSwitch("Hide NSFW folders",
            "Folders marked NSFW, and everything inside them, stay out of Files and search.",
            _settings.HideNsfwMediaFolders,
            on => _settings.HideNsfwMediaFolders = on);

        AddSettingSwitch("Blur photos until opened",
            "Thumbnails stay blurred. The full image shows only when you open it.",
            _settings.BlurMediaThumbnails,
            on => _settings.BlurMediaThumbnails = on);

        AddSettingSwitch("Offer to remove originals after importing",
            "After a file is safely in the vault, ask whether to overwrite and delete the copy it "
            + "came from. It defeats undelete tools. On an SSD it cannot guarantee the old bytes are "
            + "gone, which is what disk encryption is for.",
            _settings.OfferShredAfterImport,
            on => _settings.OfferShredAfterImport = on);

        AddSettingsHeading("This vault");

        AddSettingAction(_profiles.ActiveProfile?.Name ?? "Default",
            "The name shown in Lockwell on this PC.",
            "Rename", BeginRenameVault);

        AddSettingAction("Other vaults",
            "Each profile is a separate vault with its own master password.",
            "Switch profile", () =>
            {
                _onLock();
                _onSwitchProfile();
            });
    }

    // ---------------------------------------------------------------- security

    private void RenderSecuritySettings()
    {
        AddSettingsHeading("Locking", first: true);

        AddSettingNumber("Lock after being idle",
            "Minutes without any mouse or keyboard input.",
            _settings.AutoLockMinutes, 1,
            v => _settings.AutoLockMinutes = v);

        AddSettingSwitch("Lock when minimized",
            "Turn this off if you need the vault open while using another app.",
            _settings.LockOnMinimize,
            on => _settings.LockOnMinimize = on);

        AddSettingNumber("Clear the clipboard after",
            "Seconds until a copied value is wiped, if it is still on the clipboard.",
            _settings.ClipboardClearSeconds, 5,
            v => _settings.ClipboardClearSeconds = v);

        AddSettingsHeading("Privacy");

        AddSettingSwitch("Hide the window from screen capture",
            "The window appears blank in OBS, Discord share and recorders. It cannot stop a phone "
            + "camera pointed at your screen.",
            _settings.ExcludeFromCapture,
            on => _settings.ExcludeFromCapture = on);

        AddSettingSwitch("Scan for recorders on launch",
            "Warns you about known screen recorders and remote-access tools before you unlock. It "
            + "only knows the tools it has names for.",
            _settings.RunPreflight,
            on => _settings.RunPreflight = on);

        AddSettingsHeading("Master password");

        SettingsBody.Children.Add(new TextBlock
        {
            Text = "Changing it re-wraps the same data. Your items stay intact. Backups you already "
                   + "made still open with the old password.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 12),
        });

        var newPw = AddLabeledPassword("New master password");
        var confirmPw = AddLabeledPassword("Confirm new password", topMargin: 10);
        var change = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Change master password",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0),
        };
        change.Click += (_, _) =>
        {
            using SecureString newPassword = newPw.SecurePassword;
            using SecureString confirmPassword = confirmPw.SecurePassword;

            if (newPassword.Length < 10)
            {
                MessageBox.Show("The new password must be at least 10 characters.", "Lockwell");
                return;
            }

            if (!SecurePassword.SecureEquals(newPassword, confirmPassword))
            {
                MessageBox.Show("The new passwords do not match.", "Lockwell");
                return;
            }

            _vault.ChangePassword(newPassword);
            newPw.Clear();
            confirmPw.Clear();
            ShowToast("Master password changed.");
        };
        SettingsBody.Children.Add(change);
    }

    // ---------------------------------------------------------------- backup

    private void RenderBackupSettings()
    {
        AddSettingsHeading("Backup", first: true);

        DateTime? last = ResolveLastBackupUtc();
        string status = last is null
            ? "No backup yet."
            : (int)(DateTime.UtcNow - last.Value).TotalDays switch
            {
                <= 0 => $"Last backup today, {last.Value.ToLocalTime():HH:mm}.",
                1 => "Last backup yesterday.",
                var d => $"Last backup {d} days ago, {last.Value.ToLocalTime():d MMM yyyy}.",
            };

        SettingsBody.Children.Add(new TextBlock
        {
            Text = status,
            Style = (Style)FindResource("Body"),
            Foreground = last is null || (DateTime.UtcNow - last.Value).TotalDays >= 30
                ? (Brush)FindResource("Warning")
                : (Brush)FindResource("Text"),
        });
        SettingsBody.Children.Add(new TextBlock
        {
            // A backup does not protect against a forgotten password: the file is encrypted
            // with that same password. Only the recovery key covers that.
            Text = "An encrypted ZIP you can keep on a USB stick or in the cloud. It protects against "
                   + "a failed drive or a lost PC, not a forgotten password. Importing only adds new "
                   + "items and never replaces anything.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 4, 0, 12),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var export = new Button
        {
            Style = (Style)FindResource("AccentButton"),
            Content = "Back up now",
        };
        export.Click += (_, _) => ExportBackup();
        var import = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Import a backup",
            Margin = new Thickness(8, 0, 0, 0),
        };
        import.Click += (_, _) => BeginImportBackup();
        buttons.Children.Add(export);
        buttons.Children.Add(import);
        SettingsBody.Children.Add(buttons);

        AddSettingsHeading("Storage");
        AddSettingAction(FormatBytes(_vault.CalculateDiskSizeBytes()) + " encrypted on disk",
            "See what is using the space, from the whole vault down to a single file.",
            "Open breakdown", ShowStorage);

        AddVaultLocationSettings();
    }

    // ---------------------------------------------------------------- building blocks

    private void AddSettingsHeading(string text, bool first = false)
    {
        SettingsBody.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("H2"),
            Margin = new Thickness(0, first ? 0 : 28, 0, 10),
        });
    }

    private Grid SettingRow(string title, string description, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Body") });
        if (description.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = description,
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        grid.Children.Add(text);

        control.Margin = new Thickness(24, 0, 0, 0);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        SettingsBody.Children.Add(grid);
        return grid;
    }

    private void AddSettingSwitch(string title, string description, bool value, Action<bool> set)
    {
        var box = new CheckBox { IsChecked = value };
        box.Click += (_, _) =>
        {
            set(box.IsChecked == true);
            _applySettings();
        };
        SettingRow(title, description, box);
    }

    private void AddSettingNumber(string title, string description, int value, int minimum, Action<int> set)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("InputBox"),
            Text = value.ToString(),
            Width = 64,
            Height = 32,
            TextAlignment = TextAlignment.Center,
        };

        void Commit()
        {
            if (int.TryParse(box.Text, out int parsed) && parsed >= minimum)
            {
                set(parsed);
                _applySettings();
                value = parsed;
            }
            else
            {
                box.Text = value.ToString();
            }
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) Commit();
        };
        SettingRow(title, description, box);
    }

    private void AddSettingAction(string title, string description, string action, Action run)
    {
        var button = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = action,
        };
        button.Click += (_, _) => run();
        SettingRow(title, description, button);
    }

    /// <summary>Hide every main panel, ready for whichever one is about to show.</summary>
    private void HideAllPanels()
    {
        _settingsMode = false;
        _storageMode = false;
        _devicesMode = false;
        _mediaMode = false;
        _currentCategory = null;
        CategoryList.SelectedItem = null;

        ListColumn.Visibility = Visibility.Collapsed;
        DetailColumn.Visibility = Visibility.Collapsed;
        MediaPanel.Visibility = Visibility.Collapsed;
        StoragePanel.Visibility = Visibility.Collapsed;
        DevicesPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
    }
}
