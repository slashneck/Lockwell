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
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lockwell.Services;

/// <summary>
/// Scratch space for the one thing Lockwell cannot keep in memory: video and audio
/// playback. WPF's MediaElement can only play from a file path, so those files are
/// briefly written out decrypted. Everything else (photos, GIFs, thumbnails) is
/// decoded straight from memory and never touches disk.
///
/// Because that window cannot be closed without replacing the media stack, it is made
/// as small and as uninformative as possible:
///   - Random file names, so a snapshot of %TEMP% reveals nothing about which vault
///     items were opened. The names used to be the attachment ids themselves.
///   - Overwritten with random data before deletion.
///   - Purged on lock, on close, AND on startup, so a crash cannot leave a decrypted
///     file lying around until the next time you happen to lock.
///   - If a file is still locked by the player, it is queued for deletion at reboot
///     rather than silently left behind.
///
/// Honest limit: while a video plays, that file is readable by any process running as
/// you. See docs/THREAT-MODEL.md.
/// </summary>
public static class VaultTempFiles
{
    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    public static string RootDir =>
        Path.Combine(Path.GetTempPath(), "Lockwell");

    private const int FileAttributeTemporary = 0x100;
    private const int FileFlagDeleteOnClose = 0x04000000;

    /// <summary>
    /// Write decrypted bytes to a randomly named scratch file. The extension is kept
    /// because the media player needs it to pick a decoder.
    ///
    /// Two Windows flags do real work here, and both are worth naming:
    ///
    /// FILE_ATTRIBUTE_TEMPORARY tells the cache manager this file is not expected to
    /// last, so it tries to satisfy the whole life of the file from memory and avoid
    /// committing it to the physical disk at all. For a video that is opened, played and
    /// closed, the bytes often never reach the platter or the flash.
    ///
    /// FILE_FLAG_DELETE_ON_CLOSE makes Windows itself remove the file when the last
    /// handle closes. That covers the case the previous cleanup could not: if Lockwell
    /// is killed or crashes mid-playback, the handle closes with the process and the
    /// operating system deletes the file. Previously it survived until the next launch.
    ///
    /// The handle is shared for read, write and delete so the media player can still open
    /// the path normally while Lockwell holds it open.
    /// </summary>
    public static ScratchFile Write(string extension, byte[] data)
    {
        Directory.CreateDirectory(RootDir);

        if (!string.IsNullOrEmpty(extension) && !extension.StartsWith('.'))
            extension = "." + extension;

        // Unpredictable name: nothing in the file system links this back to a vault entry.
        string name = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string path = Path.Combine(RootDir, name + extension);

        var options = (FileOptions)(FileAttributeTemporary | FileFlagDeleteOnClose);

        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, options);
        }
        catch
        {
            // A file system that will not honour those flags still has to work; fall back
            // to a plain file, which the existing purge and shred paths already handle.
            File.WriteAllBytes(path, data);
            return new ScratchFile(path, null);
        }

        try
        {
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return new ScratchFile(path, stream);
    }

    /// <summary>
    /// A decrypted scratch file and the handle keeping it alive. Disposing it shreds and
    /// removes the file; letting the process die does the same, because the handle closes
    /// with it.
    /// </summary>
    public sealed class ScratchFile : IDisposable
    {
        private FileStream? _handle;

        internal ScratchFile(string path, FileStream? handle)
        {
            Path = path;
            _handle = handle;
        }

        public string Path { get; }

        public void Dispose()
        {
            FileStream? handle = _handle;
            _handle = null;

            if (handle is not null)
            {
                // Overwrite through the handle we already hold, then let closing it
                // trigger the delete-on-close the file was opened with.
                try
                {
                    Overwrite(handle);
                }
                catch { /* best effort */ }

                try { handle.Dispose(); } catch { /* already gone */ }
            }

            // Covers the fallback path, and anything the OS did not remove for us.
            SecureDelete(Path);
        }

        private static void Overwrite(FileStream stream)
        {
            long length = stream.Length;
            if (length <= 0) return;

            stream.Position = 0;
            byte[] buffer = new byte[(int)System.Math.Min(65536, length)];
            using var rng = RandomNumberGenerator.Create();

            long remaining = length;
            while (remaining > 0)
            {
                int chunk = (int)System.Math.Min(buffer.Length, remaining);
                rng.GetBytes(buffer.AsSpan(0, chunk));
                stream.Write(buffer, 0, chunk);
                remaining -= chunk;
            }

            stream.Flush();
        }
    }

    public static void SecureDelete(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            long length = new FileInfo(path).Length;
            if (length > 0)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[(int)Math.Min(65536, length)];
                using var rng = RandomNumberGenerator.Create();
                for (int pass = 0; pass < 2; pass++)
                {
                    stream.Position = 0;
                    long remaining = length;
                    while (remaining > 0)
                    {
                        int chunk = (int)Math.Min(buffer.Length, remaining);
                        rng.GetBytes(buffer.AsSpan(0, chunk));
                        stream.Write(buffer, 0, chunk);
                        remaining -= chunk;
                    }
                    stream.Flush(flushToDisk: true);
                }
            }
        }
        catch { /* best effort: the player may still hold the handle */ }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Still locked. Make sure Windows removes it at reboot rather than
            // leaving a decrypted file behind indefinitely.
            try { MoveFileEx(path, null, MoveFileDelayUntilReboot); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Remove every scratch file. Called on lock, on exit, and on startup so that a
    /// crash or power loss cannot leave decrypted media readable on disk.
    /// </summary>
    public static void PurgeAll()
    {
        if (!Directory.Exists(RootDir)) return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(RootDir))
                SecureDelete(file);
        }
        catch { /* best effort */ }
    }
}
