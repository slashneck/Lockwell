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
        OverlayBody.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("Stroke"),
            Margin = new Thickness(0, 18, 0, 14),
        });
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Vault location",
            Style = (Style)FindResource("H2"),
        });

        OverlayBody.Children.Add(new TextBlock
        {
            Text = _vault.VaultDir,
            Style = (Style)FindResource("Body"),
            Margin = new Thickness(0, 6, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        });

        long size = MeasureCurrentVaultBytes();
        OverlayBody.Children.Add(new TextBlock
        {
            Text = size > 0
                ? $"{VaultRelocation.Megabytes(size)} on disk."
                : "Size could not be measured.",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 10),
        });

        OverlayBody.Children.Add(new TextBlock
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
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 230,
        };
        moveBtn.Click += (_, _) => BeginMoveVault();
        OverlayBody.Children.Add(moveBtn);

        OverlayBody.Children.Add(new TextBlock
        {
            Text = "A vault on a removable drive is only readable while that drive is "
                 + "plugged in. If it is the only copy, keep a backup somewhere else.",
            Style = (Style)FindResource("Caption"),
            Foreground = (Brush)FindResource("Warning"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });
    }

    private long MeasureCurrentVaultBytes()
    {
        try
        {
            return VaultRelocation.Prepare(_vault.VaultDir, _vault.VaultDir).TotalBytes;
        }
        catch (Exception)
        {
            return 0;
        }
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
            Title = "Choose the folder the vault should move to",
        };
        if (dlg.ShowDialog() != true) return;

        string destination = dlg.FolderName;

        string? refusal = VaultRelocation.Validate(_vault.VaultDir, destination);
        if (refusal is not null)
        {
            MessageBox.Show(refusal, "Lockwell", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            + "It stays encrypted the whole way. Lockwell will copy it, check the copy, "
            + "and then use the new location. Nothing is removed from the old one until "
            + "you confirm.\n\n"
            + "The vault will lock, and you will unlock it again afterwards.",
            "Lockwell", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (go != MessageBoxResult.OK) return;

        // Lock before touching anything. An open vault holds its file, and copying a
        // vault that is still being written to is how a copy ends up subtly wrong.
        CloseOverlay();
        _vault.Lock();

        var token = ShowCancellableProgress("Moving vault", "Preparing...");
        List<string> problems;

        try
        {
            var reporter = new Progress<OperationProgress>(ApplyOperationProgress);
            problems = await Task.Run(() =>
            {
                VaultRelocation.Copy(plan, reporter, token);
                return VaultRelocation.Verify(plan);
            }, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            HideProgress();
            CleanUpAbandonedCopy(plan);
            MessageBox.Show(
                "The move was stopped. Your vault is still in its original location and "
                + "nothing was changed.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Information);
            _onLock();
            return;
        }
        catch (Exception ex)
        {
            HideProgress();
            MessageBox.Show(
                "The vault could not be copied: " + ex.Message + "\n\n"
                + "Nothing was changed. Your vault is still in its original location.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Error);
            _onLock();
            return;
        }

        await FinishProgressAsync("Checking the copy...").ConfigureAwait(true);

        if (problems.Count > 0)
        {
            string shown = string.Join("\n", problems.Take(5));
            if (problems.Count > 5) shown += $"\n...and {problems.Count - 5} more.";

            MessageBox.Show(
                "The copy did not come out right, so nothing was switched over:\n\n"
                + shown + "\n\n"
                + "Your vault is untouched in its original location. The incomplete copy "
                + $"at {plan.Destination} can be deleted.",
                "Lockwell", MessageBoxButton.OK, MessageBoxImage.Error);
            _onLock();
            return;
        }

        // Verified. Now, and only now, the profile changes hands.
        string previous = plan.Source;
        profile.VaultDir = plan.Destination;
        _settings.Save();

        OfferToRemoveOldVault(plan, previous);

        // Back to the gate, which builds a fresh VaultManager from the profile's new
        // path. Carrying on with the old one would leave the app reading a folder that
        // may no longer hold a vault.
        //
        // Locking is what returns to the gate, and the vault is already locked by this
        // point, so the save-before-lock step there correctly does nothing. That order
        // matters: saving first would write the vault back into the folder this move may
        // have just emptied.
        _onLock();
    }

    private void OfferToRemoveOldVault(VaultRelocation.Plan plan, string previous)
    {
        var answer = MessageBox.Show(
            $"The vault is now at:\n{plan.Destination}\n\n"
            + $"The original copy is still at:\n{previous}\n\n"
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

    /// <summary>
    /// Clear away a copy that was abandoned part-way. Best effort, and deliberately
    /// never touches anything outside the destination it just created.
    /// </summary>
    private static void CleanUpAbandonedCopy(VaultRelocation.Plan plan)
    {
        foreach (string name in VaultRelocation.VaultEntries)
        {
            string path = Path.Combine(plan.Destination, name);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception)
            {
                // Leaving a partial copy behind is untidy, not dangerous. The original
                // is what matters and it has not been touched.
            }
        }
    }
}
