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
using System.Windows;
using Lockwell.Services;

namespace Lockwell.Views;

/// <summary>
/// What happens to the file you imported *from*, once its contents are safely in the
/// vault.
///
/// This exists because of a gap that is easy to miss. Putting a photo in the vault does
/// not remove it from wherever it came from, so the encrypted copy and a plain copy sit
/// side by side and the vault protects neither. Closing that gap has to be deliberate,
/// though: deleting the source is irreversible, and the standing rule in this project is
/// that nothing destroys the only copy of something as a side effect of something else.
///
/// So it is an offer, made after the import has already succeeded, off unless turned on,
/// and it says plainly what it can and cannot promise. See OriginalFileCleanup for why
/// "securely erased" is not a claim this app is willing to make on modern storage.
/// </summary>
public partial class VaultShellView
{
    /// <summary>
    /// Offer to remove the files an import came from, and warn about copies elsewhere.
    /// Does nothing when nothing was imported.
    /// </summary>
    private void OfferOriginalCleanup(IReadOnlyList<string> importedFrom)
    {
        if (importedFrom.Count == 0) return;

        var existing = importedFrom.Where(File.Exists).ToList();
        if (existing.Count == 0) return;

        // Copies somewhere the deletion cannot reach are worth mentioning whether or not
        // the user wants the local original removed: it is the one that gets found later.
        var elsewhere = OriginalFileCleanup.FindOtherCopies(existing);

        if (!_settings.OfferShredAfterImport)
        {
            WarnAboutOtherCopies(elsewhere);
            return;
        }

        string what = existing.Count == 1
            ? $"\"{Path.GetFileName(existing[0])}\""
            : $"{existing.Count} files";

        string message =
            $"{what} is now encrypted in your vault. The file it came from is still on this " +
            "computer, where anything can open it.\n\n" +
            "Lockwell can overwrite it and delete it.\n\n" +
            "What this does: recovery tools that undelete files will not get it back.\n" +
            "What it does not do: on an SSD, overwriting cannot guarantee the old bytes are " +
            "gone from the drive itself, because the drive decides where writes physically " +
            "land. Full-disk encryption is what protects against that, not this.\n\n" +
            "Remove the original now?";

        if (elsewhere.Count > 0)
        {
            message += "\n\nNote: " + DescribeOtherCopies(elsewhere);
        }

        var answer = MessageBox.Show(message, "Lockwell",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            WarnAboutOtherCopies(elsewhere);
            return;
        }

        int removed = 0;
        var stubborn = new List<string>();

        foreach (string path in existing)
        {
            if (OriginalFileCleanup.Shred(path)) removed++;
            else stubborn.Add(Path.GetFileName(path));
        }

        string result = removed == 1
            ? "The original has been overwritten and deleted."
            : $"{removed} originals have been overwritten and deleted.";

        if (stubborn.Count > 0)
        {
            result += $"\n\n{stubborn.Count} could not be removed, most likely because " +
                      "another program still has them open. Windows will delete those at " +
                      "the next restart.";
        }

        if (elsewhere.Count > 0) result += "\n\n" + DescribeOtherCopies(elsewhere);

        MessageBox.Show(result, "Lockwell");
    }

    private static void WarnAboutOtherCopies(IReadOnlyList<CopyWarning> elsewhere)
    {
        if (elsewhere.Count == 0) return;

        MessageBox.Show(
            "Added to your vault.\n\n" + DescribeOtherCopies(elsewhere),
            "Lockwell");
    }

    /// <summary>
    /// One sentence naming the services that still hold a copy. Deliberately does not list
    /// every file: the point is that a synced folder is involved at all.
    /// </summary>
    private static string DescribeOtherCopies(IReadOnlyList<CopyWarning> elsewhere)
    {
        var services = elsewhere
            .Select(w => w.Where)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string names = services.Count == 1
            ? services[0]
            : string.Join(", ", services.Take(services.Count - 1)) + " and " + services[^1];

        string detail = elsewhere
            .Select(w => w.Detail)
            .Distinct(StringComparer.Ordinal)
            .First();

        return $"Some of these files came from a folder synced with {names}. {detail}";
    }
}
