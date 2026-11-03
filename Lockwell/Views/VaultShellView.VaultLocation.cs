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
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lockwell.Services;
using Microsoft.Win32;

namespace Lockwell.Views;

/// <summary>
/// The Settings block that moves this vault to another drive.
///
/// The thing worth saying out loud here, because it is the question anyone asks: the
/// vault is never decrypted to move it. It is copied exactly as it sits on disk, and the
/// profile is pointed at the new place. A vault on a USB stick is no less protected than
/// one on the internal drive, and losing the stick costs no more than losing the PC would.
///
/// All the care in <see cref="VaultRelocation"/> is about not destroying the only copy of
/// something. The UI's job is to make the order of events visible: copy, check, switch,
/// and only then offer to remove the original.
/// </summary>
public partial class VaultShellView
{
    private void AddVaultLocationSettings()
    {
        AddSettingsHeading("Vault location");

        FormHost.Children.Add(new TextBlock
        {
            Text = _vault.VaultDir,
            Style = (Style)FindResource("Body"),
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        });

        FormHost.Children.Add(new TextBlock
        {
            Text = "Move this vault to another drive, such as a USB stick or an external "
                 + "disk. It is copied exactly as it is stored, still encrypted the whole "
                 + "way, and your master password is not needed to move it. The copy is "
                 + "checked before anything is switched over, and the original is only "
                 + "removed if you say so afterwards.",
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var moveBtn = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Move vault to another folder...",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        moveBtn.Click += (_, _) => BeginMoveVault();
        FormHost.Children.Add(moveBtn);

        FormHost.Children.Add(new TextBlock
        {
            Text = "A vault on a removable drive is only readable while that drive is "
                 + "plugged in. If it is the only copy, keep a backup somewhere else.",
            Style = (Style)FindResource("Caption"),
            Foreground = (Brush)FindResource("Warning"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });
    }

    // ---------------------------------------------------------------- the move

    private async void BeginMoveVault()
    {
        if (_busyOperation) return;

        var profile = _profiles.ActiveProfile;
        if (profile is null)
        {
            MessageBox.Show("No active profile to move.", "Lockwell");
            return;
        }

        var dlg = new OpenFolderDialog
        {
            Title = "Choose where the vault should move to",
        };
        if (dlg.ShowDialog() != true) return;

        string destination = VaultRelocation.ResolveDestination(dlg.FolderName);

        string? refusal = VaultRelocation.Validate(_vault.VaultDir, destination);
        if (refusal is not null)
        {
            MessageBox.Show(refusal, "Lockwell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Before anything is measured, so the copy holds every edit made so far.
        try
        {
            _vault.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not save the vault before moving it: " + ex.Message, "Lockwell",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        VaultRelocation.Plan plan;
        try
        {
            plan = VaultRelocation.Prepare(_vault.VaultDir, destination);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not read the vault: " + ex.Message, "Lockwell",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!VaultRelocation.HasRoomFor(plan, out long free))
        {
            MessageBox.Show(
                $"That drive has {VaultRelocation.Megabytes(free)} free, and the vault "
                + $"needs {VaultRelocation.Megabytes(plan.TotalBytes)}.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var go = MessageBox.Show(
            $"Move this vault to:\n{plan.Destination}\n\n"
            + $"{plan.Files.Count} file(s), {VaultRelocation.Megabytes(plan.TotalBytes)}.\n\n"
            + "It stays encrypted the whole way. Lockwell will copy it, check every file, "
            + "and then use the new location. Nothing is removed from the old one unless "
            + "you confirm.\n\n"
            + "The vault will lock afterwards, and you will unlock it again.",
            "Lockwell", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (go != MessageBoxResult.OK) return;

        CloseOverlay();

        var token = ShowCancellableProgress("Moving vault", "Preparing...");
        List<string> problems;

        try
        {
            var reporter = new Progress<OperationProgress>(ApplyOperationProgress);
            problems = await Task.Run(() =>
            {
                VaultRelocation.Copy(plan, reporter, token);
                return VaultRelocation.Verify(plan, reporter, token);
            }, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            HideProgress();
            VaultRelocation.DeleteAbandonedCopy(plan);
            MessageBox.Show(
                "The move was stopped. Your vault is still in its original location and "
                + "nothing was changed.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        catch (Exception ex)
        {
            HideProgress();
            VaultRelocation.DeleteAbandonedCopy(plan);
            MessageBox.Show(
                "The vault could not be copied: " + ex.Message + "\n\n"
                + "Nothing was changed. Your vault is still in its original location. A USB "
                + "stick formatted as FAT32 cannot hold any file over 4 GB.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (problems.Count > 0)
        {
            HideProgress();
            VaultRelocation.DeleteAbandonedCopy(plan);

            string shown = string.Join("\n", problems.Take(5));
            if (problems.Count > 5) shown += $"\n...and {problems.Count - 5} more.";

            MessageBox.Show(
                "The copy did not come out right, so it was thrown away and nothing was "
                + "switched over:\n\n" + shown + "\n\n"
                + "Your vault is untouched in its original location. If this drive is a USB "
                + "stick, it may be failing.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await FinishProgressAsync("Every file checked.").ConfigureAwait(true);

        // The order here matters. Lock first: the app's normal lock saves the vault, and
        // that save must never write into a folder this move may be about to empty.
        _vault.Lock();
        string previous = plan.Source;
        profile.VaultDir = plan.Destination;
        _settings.Save();

        OfferToRemoveOldVault(plan, previous);

        // Back to the gate, which builds a fresh VaultManager from the profile's new path.
        _onLock();
    }

    private void OfferToRemoveOldVault(VaultRelocation.Plan plan, string previous)
    {
        var answer = MessageBox.Show(
            $"The vault is now at:\n{plan.Destination}\n\n"
            + $"The original copy is still at:\n{previous}\n\n"
            + "Every file was checked against it first.\n\n"
            + "Remove it? It is still encrypted and still opens with your master "
            + "password, so leaving it means there are two copies of your data that both "
            + "work.\n\n"
            + "Choose No to keep it as a backup.",
            "Lockwell", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            MessageBox.Show(
                $"Kept. The old copy is still at:\n{previous}\n\n"
                + "Remember it opens with the same password.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var failures = VaultRelocation.DeleteSource(plan);
        MessageBox.Show(
            failures.Count == 0
                ? $"Moved. The vault now lives at:\n{plan.Destination}"
                : $"The vault now lives at:\n{plan.Destination}\n\n"
                  + "Some of the old files could not be removed:\n"
                  + string.Join("\n", failures.Take(5))
                  + $"\n\nYou can delete what is left at {previous} yourself.",
            "Lockwell", MessageBoxButton.OK,
            failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
