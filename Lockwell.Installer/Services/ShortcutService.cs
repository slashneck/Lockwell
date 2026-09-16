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

using System.Runtime.InteropServices;

namespace Lockwell.Installer.Services;

internal static class ShortcutService
{
    public static void CreateOrUpdateOnDesktop(string shortcutName, string targetExePath, string? iconPath = null)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        WriteShortcut(Path.Combine(desktop, shortcutName.Trim() + ".lnk"), targetExePath, iconPath);
    }

    public static void CreateOrUpdateInStartMenu(string shortcutName, string targetExePath, string? iconPath = null)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), shortcutName.Trim());
        Directory.CreateDirectory(folder);
        WriteShortcut(Path.Combine(folder, shortcutName.Trim() + ".lnk"), targetExePath, iconPath);
    }

    public static void RemoveDesktopShortcut(string shortcutName)
    {
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), shortcutName.Trim() + ".lnk"));
    }

    public static void RemoveStartMenuShortcut(string shortcutName)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), shortcutName.Trim());
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    public static string? ResolveIconLocationNextToExe(string exePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        foreach (var name in new[] { "app.ico", "app-admin.ico" })
        {
            var ico = Path.Combine(dir, name);
            if (File.Exists(ico))
            {
                return $"{ico},0";
            }
        }

        return $"{Path.GetFullPath(exePath)},0";
    }

    private static void WriteShortcut(string lnkPath, string targetExePath, string? iconPath)
    {
        var exe = Path.GetFullPath(targetExePath);
        var t = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available.");

        dynamic shell = Activator.CreateInstance(t)
            ?? throw new InvalidOperationException("Could not create WScript.Shell.");

        try
        {
            dynamic sc = shell.CreateShortcut(lnkPath);
            try
            {
                sc.TargetPath = exe;
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir))
                {
                    sc.WorkingDirectory = dir;
                }

                sc.IconLocation = string.IsNullOrWhiteSpace(iconPath) ? $"{exe},0" : iconPath;
                sc.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(sc);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }
}
