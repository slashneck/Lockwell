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

using System.Threading;
using Lockwell.Models;
using Lockwell.Services;

namespace Lockwell.Tests;

/// <summary>
/// Taking a folder out of the vault, and above all deciding whether it is safe to remove it
/// afterwards.
///
/// The first version exported only the first attachment of each item and judged success by
/// counting, so an item holding two files lost one with the count still agreeing, and the
/// app then offered to delete the original. These checks use a real vault with an item
/// holding several attachments, because that is the case that was wrong.
/// </summary>
internal static class ExportChecks
{
    public static void Run()
    {
        Check.Section("70. A folder export is complete before it can cost anything");

        string scratch = Check.Scratch("export");
        try
        {
            string vaultDir = Path.Combine(scratch, "vault");
            string output = Path.Combine(scratch, "out");
            Directory.CreateDirectory(vaultDir);
            Directory.CreateDirectory(output);

            var vault = new VaultManager(vaultDir);
            using (var password = Check.Secret("a long enough passphrase"))
                vault.CreateVault(password, withRecoveryKey: false);

            var category = vault.Data.Categories.First(c => c.IsMedia);
            var root = new MediaFolder { CategoryId = category.Id, Name = "Project" };
            var sub = new MediaFolder { CategoryId = category.Id, Name = "Clips", ParentFolderId = root.Id };
            var empty = new MediaFolder { CategoryId = category.Id, Name = "Empty", ParentFolderId = root.Id };
            var hostile = new MediaFolder { CategoryId = category.Id, Name = "..", ParentFolderId = root.Id };
            vault.Data.MediaFolders.AddRange(new[] { root, sub, empty, hostile });

            byte[] rar = Bytes(4096, 1);
            byte[] video = Bytes(8192, 2);
            byte[] clip = Bytes(2048, 3);

            AttachmentRef Add(string name, byte[] data)
            {
                string path = Path.Combine(scratch, name);
                File.WriteAllBytes(path, data);
                return vault.AddAttachment(path);
            }

            // One item, a project archive and a video of it. The case that used to lose a file.
            vault.Data.Entries.Add(new VaultEntry
            {
                CategoryId = category.Id,
                MediaFolderId = root.Id,
                Title = "Build",
                Kind = EntryKind.Media,
                Attachments = { Add("project.rar", rar), Add("demo.mp4", video) },
            });
            vault.Data.Entries.Add(new VaultEntry
            {
                CategoryId = category.Id,
                MediaFolderId = sub.Id,
                Title = "Clip",
                Kind = EntryKind.Media,
                Attachments = { Add("clip.mp4", clip) },
            });

            var plan = FolderExporter.BuildPlan(vault.Data, root);
            Check.That("every attachment of every item is planned (3)", plan.Files.Count == 3);
            Check.That("empty sub-folders are kept", plan.Directories.Any(d => d.EndsWith("Empty")));

            var result = FolderExporter.Run(plan, root.Name, output, vault.ReadAttachment, null, CancellationToken.None);
            string landed = result.Destination;

            Check.That("all of them were written", result.Exported == 3 && result.Failures.Count == 0);
            Check.That("a RAR comes out byte for byte",
                File.ReadAllBytes(Path.Combine(landed, "project.rar")).AsSpan().SequenceEqual(rar));
            Check.That("so does the second file on the same item",
                File.ReadAllBytes(Path.Combine(landed, "demo.mp4")).AsSpan().SequenceEqual(video));
            Check.That("sub-folder files land in their sub-folder",
                File.Exists(Path.Combine(landed, "Clips", "clip.mp4")));
            Check.That("a complete copy may offer removal", result.IsCompleteCopyOf(plan));

            Check.That("a folder named .. cannot walk out of the export",
                Directory.GetDirectories(output).Length == 1
                && Directory.Exists(Path.Combine(landed, "Folder")));
            Check.That("a hostile file name is neutralised",
                FolderExporter.SafeName(@"..\..\evil.exe", "File") == ".._.._evil.exe"
                && FolderExporter.SafeName("NUL.txt", "File") == "_NUL.txt"
                && FolderExporter.SafeName("...", "File") == "File");

            var again = FolderExporter.Run(plan, root.Name, output, vault.ReadAttachment, null, CancellationToken.None);
            Check.That("a second export does not overwrite the first", again.Destination != landed);

            // An attachment whose blob has gone must not stop the rest, and must block removal.
            var victim = plan.Files[0].Attachment;
            File.Delete(Path.Combine(vault.AttachmentsDir, victim.Id + ".lwa"));
            var damaged = FolderExporter.Run(plan, root.Name, output, vault.ReadAttachment, null, CancellationToken.None);
            Check.That("an unreadable file is reported", damaged.Failures.Count == 1);
            Check.That("the others still come out", damaged.Exported == 2);
            Check.That("and removal is not offered", !damaged.IsCompleteCopyOf(plan));

            // Stopping part-way must not count as finished.
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            var stopped = FolderExporter.Run(plan, root.Name, output, vault.ReadAttachment, null, stop.Token);
            Check.That("a stopped export cannot offer removal", !stopped.IsCompleteCopyOf(plan));

            // Text in the vault has no file to land in.
            vault.Data.Entries.Add(new VaultEntry
            {
                CategoryId = category.Id,
                MediaFolderId = sub.Id,
                Title = "Notes only",
                Notes = "exists nowhere else",
            });
            var withNotes = FolderExporter.BuildPlan(vault.Data, root);
            Check.That("an item holding text is noticed", withNotes.VaultOnlyItems.Contains("Notes only"));

            var noted = new FolderExporter.Result { Exported = withNotes.Files.Count, Destination = "x" };
            Check.That("and removal is not offered for it", !noted.IsCompleteCopyOf(withNotes));

            vault.Lock();
        }
        finally
        {
            Check.Cleanup(scratch);
        }
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
