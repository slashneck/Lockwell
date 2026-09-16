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

using Lockwell.Models;
using Lockwell.Services;

namespace Lockwell.Tests;

/// <summary>
/// Pure logic that can corrupt data if wrong: folder reparenting and drag-reorder
/// index maths. Both were deliberately lifted out of the UI so they could be tested.
/// </summary>
internal static class LogicChecks
{
    public static void Run()
    {
        FolderTree();
        ReorderMaths();
        TempFiles();
    }

    private static void FolderTree()
    {
        Check.Section("9. Media folder tree safety");
        Check.Note("A folder dropped into its own subtree would orphan that branch.");

        var photos = new MediaFolder { Id = "photos", Name = "Photos" };
        var y2024 = new MediaFolder { Id = "y2024", Name = "2024", ParentFolderId = "photos" };
        var summer = new MediaFolder { Id = "summer", Name = "Summer", ParentFolderId = "y2024" };
        var docs = new MediaFolder { Id = "docs", Name = "Docs" };
        var tree = new List<MediaFolder> { photos, y2024, summer, docs };

        Check.That("descendant: summer is under photos",
            MediaFolderTree.IsDescendantOf(tree, "summer", "photos"));
        Check.That("descendant: photos is NOT under summer",
            !MediaFolderTree.IsDescendantOf(tree, "photos", "summer"));

        Check.That("BLOCKS dropping photos into its child",
            !MediaFolderTree.CanReparent(tree, "photos", "y2024"));
        Check.That("BLOCKS dropping photos into its grandchild",
            !MediaFolderTree.CanReparent(tree, "photos", "summer"));
        Check.That("BLOCKS dropping a folder into itself",
            !MediaFolderTree.CanReparent(tree, "photos", "photos"));
        Check.That("BLOCKS a no-op move to its current parent",
            !MediaFolderTree.CanReparent(tree, "y2024", "photos"));
        Check.That("BLOCKS a move to an unknown folder id",
            !MediaFolderTree.CanReparent(tree, "photos", "does-not-exist"));

        Check.That("ALLOWS moving into an unrelated folder",
            MediaFolderTree.CanReparent(tree, "photos", "docs"));
        Check.That("ALLOWS moving a nested folder out to the root",
            MediaFolderTree.CanReparent(tree, "summer", null));

        // Already-corrupted data must not hang the UI thread.
        var a = new MediaFolder { Id = "a", ParentFolderId = "b" };
        var b = new MediaFolder { Id = "b", ParentFolderId = "a" };
        var cyclic = new List<MediaFolder> { a, b };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MediaFolderTree.IsDescendantOf(cyclic, "a", "zzz");
        sw.Stop();
        Check.That($"a pre-existing cycle terminates instead of hanging ({sw.ElapsedMilliseconds}ms)",
            sw.ElapsedMilliseconds < 1000);
    }

    private static void ReorderMaths()
    {
        Check.Section("10. Drag-to-reorder index maths");
        Check.Note("Removing the dragged item shifts every index after it.");

        var baseline = new List<string> { "a", "b", "c", "d" };

        void Order(string label, string moved, string target, bool after, string expected)
        {
            string actual = string.Join("", MediaOrdering.Reorder(baseline, moved, target, after));
            Check.That($"{label} -> {actual}", actual == expected);
        }

        Order("move a to after c", "a", "c", true, "bcad");
        Order("move a to before c", "a", "c", false, "bacd");
        Order("move a to after d (end)", "a", "d", true, "bcda");
        Order("move d to before b", "d", "b", false, "adbc");
        Order("move d to after b", "d", "b", true, "abdc");
        Order("move c to before a (start)", "c", "a", false, "cabd");
        Order("move a before b (already there)", "a", "b", false, "abcd");
        Order("move b after a (already there)", "b", "a", true, "abcd");

        Check.That("dropping an item onto itself is a no-op",
            string.Join("", MediaOrdering.Reorder(baseline, "b", "b", true)) == "abcd");
        Check.That("unknown moved id leaves order untouched",
            string.Join("", MediaOrdering.Reorder(baseline, "zz", "b", true)) == "abcd");
        Check.That("unknown target id leaves order untouched",
            string.Join("", MediaOrdering.Reorder(baseline, "a", "zz", true)) == "abcd");
        Check.That("the source list is never mutated in place",
            string.Join("", baseline) == "abcd");

        var shuffled = MediaOrdering.Reorder(baseline, "a", "d", true);
        Check.That("no items lost or duplicated by a reorder",
            shuffled.Count == 4 && shuffled.Distinct().Count() == 4 && baseline.All(shuffled.Contains));
    }

    private static void TempFiles()
    {
        Check.Section("11. Temp scratch files (decrypted media on disk)");

        VaultTempFiles.PurgeAll();

        byte[] payload = System.Text.Encoding.UTF8.GetBytes("SENSITIVE-DECRYPTED-VIDEO-BYTES");
        var f1 = VaultTempFiles.Write(".mp4", payload);
        var f2 = VaultTempFiles.Write(".mp4", payload);

        string n1 = Path.GetFileNameWithoutExtension(f1.Path);
        string n2 = Path.GetFileNameWithoutExtension(f2.Path);

        Check.That("scratch file is created", File.Exists(f1.Path));
        Check.That("two writes get different unpredictable names", n1 != n2);
        Check.That("name is random hex, not derived from the attachment",
            n1.Length == 32 && n1.All(c => "0123456789abcdef".Contains(c)));
        Check.That("extension is preserved for the decoder", Path.GetExtension(f1.Path) == ".mp4");

        // The player has to be able to open the path while Lockwell still holds the
        // handle, so the share mode matters as much as the contents.
        Check.That("contents are readable by a player while open (known, documented limit)",
            System.Text.Encoding.UTF8.GetString(
                File.ReadAllBytes(f1.Path)).Contains("SENSITIVE"));

        f1.Dispose();
        Check.That("disposing the scratch file removes it", !File.Exists(f1.Path));

        f2.Dispose();

        var leftover = VaultTempFiles.Write(".mp4", payload);
        string leftoverPath = leftover.Path;
        Check.That("a leftover exists before the startup purge", File.Exists(leftoverPath));

        // Deliberately not disposed: this stands in for a crash mid-playback. The handle
        // carries delete-on-close, so Windows removes it when the process dies; the purge
        // is the belt to that braces.
        VaultTempFiles.PurgeAll();
        Check.That("startup purge clears crash leftovers", !File.Exists(leftoverPath));
        Check.That("no scratch files remain at all",
            !Directory.Exists(VaultTempFiles.RootDir) ||
            Directory.GetFiles(VaultTempFiles.RootDir).Length == 0);
    }
}
