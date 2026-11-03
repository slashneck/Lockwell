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

using System.Text.Json;
using Lockwell.Models;
using Lockwell.Services;

namespace Lockwell.Tests;

/// <summary>
/// A vault made by an older release still opens, whole.
///
/// The fixture in Fixtures/vault-1.2.0 was written by the real 1.2.0 code, built from that tag.
/// It holds invented data under a password written in this file, so publishing it costs
/// nothing. A vault created by today's code cannot prove this: it only shows the code agrees
/// with itself. The bytes of this one do not change when the code does.
/// </summary>
internal static class CompatibilityChecks
{
    private const string FixturePassword = "fixture-password-not-a-secret";

    public static void Run()
    {
        Check.Section("72. A vault made by Lockwell 1.2.0 still opens, with everything in it");

        string? root = FindFixture();
        Check.That("the 1.2.0 fixture is present", root is not null);
        if (root is null) return;

        string scratch = Check.Scratch("compat");
        try
        {
            // Always a copy. The fixture itself must never be rewritten by a test.
            CopyTree(root, scratch);

            var vault = new VaultManager(scratch);
            bool opened;
            using (var password = Check.Secret(FixturePassword))
                opened = vault.Unlock(password);

            Check.That("it unlocks with its password", opened);
            if (!opened) return;

            CheckContents(vault);

            // Saving must not lose anything, and neither must opening it again.
            vault.Save();
            vault.Lock();

            var again = new VaultManager(scratch);
            using (var password = Check.Secret(FixturePassword))
                Check.That("it opens again after being saved by this build", again.Unlock(password));

            CheckContents(again);
            again.Lock();

            OldSettings();
        }
        finally
        {
            Check.Cleanup(scratch);
        }
    }

    private static void CheckContents(VaultManager vault)
    {
        var data = vault.Data;

        var login = data.Entries.FirstOrDefault(e => e.Title == "Old login");
        Check.That("an old login is there", login is not null);
        Check.That("its secret field is intact",
            login?.Fields.Any(f => f.IsSecret && f.Value == "old-password-value") == true);
        Check.That("its note is intact", login?.Notes == "An old note.");
        Check.That("it is still a favorite", login?.Favorite == true);

        var rar = login?.Attachments.FirstOrDefault(a => a.FileName == "project.rar");
        Check.That("its RAR attachment decrypts", rar is not null && vault.ReadAttachment(rar).Length == 3000);
        var video = login?.Attachments.FirstOrDefault(a => a.FileName == "demo.mp4");
        Check.That("its second attachment decrypts", video is not null && vault.ReadAttachment(video).Length == 5000);

        Check.That("a custom section keeps its name and kind",
            data.Categories.Any(c => c.Name == "Old custom section" && c.IsCustom));
        Check.That("its item stays in it",
            data.Entries.Any(e => e.Title == "Old custom item"
                                  && e.CategoryId == data.Categories.First(c => c.Name == "Old custom section").Id));

        // The new interface shows this section as "Files". The data must not be renamed, or
        // the phone and an older build would no longer recognise it.
        Check.That("the media section is not renamed in the data",
            data.Categories.Any(c => c.IsMedia && c.Name == "Media vault"));

        var folder = data.MediaFolders.FirstOrDefault(f => f.Name == "Old folder");
        Check.That("a folder keeps its settings",
            folder is { Kind: MediaFolderKind.Photos, IsFavorite: true });
        Check.That("a sub-folder keeps its parent and order",
            data.MediaFolders.Any(f => f.Name == "Old inner folder" && f.ParentFolderId == folder?.Id && f.Order == 3));
        Check.That("an NSFW mark survives",
            data.MediaFolders.Any(f => f.Name == "Old private folder" && f.IsNsfw));

        var picture = data.Entries.FirstOrDefault(e => e.Title == "Old picture");
        Check.That("a file stays in its folder, in its place",
            picture is not null && picture.MediaFolderId == folder?.Id && picture.MediaSortOrder == 7);
        Check.That("and decrypts", picture is not null && vault.ReadAttachment(picture.Attachments[0]).Length == 2000);
    }

    /// <summary>A settings file written before this interface existed.</summary>
    private static void OldSettings()
    {
        const string old = """
            {"ExcludeFromCapture":false,"AutoLockMinutes":17,"ClipboardClearSeconds":45,
             "RunPreflight":false,"LockOnMinimize":false,"HideNsfwMediaFolders":false,
             "MediaSortMode":1,"MediaSortDescending":true,"SomethingFromTheFuture":123,
             "Profiles":[{"Id":"abc","Name":"Mine","VaultDir":"D:\\Vault"}],"ActiveProfileId":"abc"}
            """;

        var settings = JsonSerializer.Deserialize(old, AppSettingsJsonContext.Default.AppSettings);

        Check.That("old settings are read", settings is not null);
        Check.That("preferences carry over",
            settings is { AutoLockMinutes: 17, ClipboardClearSeconds: 45, ExcludeFromCapture: false, HideNsfwMediaFolders: false });
        Check.That("the sort order carries over",
            settings is { MediaSortMode: MediaSortMode.Name, MediaSortDescending: true });
        Check.That("profiles carry over with their paths",
            settings?.Profiles.SingleOrDefault() is { Name: "Mine", VaultDir: @"D:\Vault" });
        Check.That("the new view setting starts at its default", settings?.FilesListView == false);
    }

    private static string? FindFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && dir is not null; depth++)
        {
            string candidate = Path.Combine(dir.FullName, "Lockwell.Tests", "Fixtures", "vault-1.2.0");
            if (File.Exists(Path.Combine(candidate, "vault.lwv"))) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
