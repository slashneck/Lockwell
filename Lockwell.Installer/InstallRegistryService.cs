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
using Microsoft.Win32;
using Lockwell.Installer.Services;



namespace Lockwell.Installer;



internal static class InstallRegistryService

{

    public const string UninstallKeyName = "Lockwell";

    public const string Publisher = "Lockwell";

    public const string DisplayName = "Lockwell";



    private const string UninstallExeName = "LockwellSetup.exe";

    private static readonly string UninstallKeyPath =

        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}";



    public static string? ReadInstallLocation()

    {

        foreach (var hive in RegistryHivesForRead())

        {

            using var key = hive.OpenSubKey(UninstallKeyPath);

            var loc = key?.GetValue("InstallLocation") as string;

            if (!string.IsNullOrWhiteSpace(loc))

            {

                return loc;

            }

        }



        return null;

    }



    public static void RegisterInstall(string installDirectory, Version displayVersion, string uninstallExePath)

    {

        WriteUninstallKey(Registry.CurrentUser, installDirectory, displayVersion, uninstallExePath);



        if (InstallerElevation.IsProcessElevated())

        {

            WriteUninstallKey(Registry.LocalMachine, installDirectory, displayVersion, uninstallExePath);

        }

    }



    public static void UnregisterInstall()

    {

        TryDeleteUninstallKey(Registry.CurrentUser);

        TryDeleteUninstallKey(Registry.LocalMachine);

    }



    public static string CopyUninstallerToInstallDir(string installDirectory)

    {

        var source = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))

        {

            throw new InvalidOperationException("Could not locate the installer executable.");

        }



        Directory.CreateDirectory(installDirectory);

        var dest = Path.Combine(installDirectory, UninstallExeName);

        File.Copy(source, dest, overwrite: true);

        return dest;

    }



    private static IEnumerable<RegistryKey> RegistryHivesForRead()

    {

        yield return Registry.LocalMachine;

        yield return Registry.CurrentUser;

    }



    private static void WriteUninstallKey(

        RegistryKey hive,

        string installDirectory,

        Version displayVersion,

        string uninstallExePath)

    {

        using var key = hive.CreateSubKey(UninstallKeyPath, writable: true)

            ?? throw new InvalidOperationException($"Could not write uninstall registry key ({hive.Name}).");



        var installRoot = installDirectory.TrimEnd('\\') + "\\";

        key.SetValue("DisplayName", DisplayName);

        key.SetValue("DisplayVersion", displayVersion.ToString(3));

        key.SetValue("Publisher", Publisher);

        key.SetValue("InstallLocation", installRoot);

        key.SetValue("UninstallString", $"\"{uninstallExePath}\" /uninstall");

        key.SetValue("QuietUninstallString", $"\"{uninstallExePath}\" /uninstall /quiet");

        key.SetValue("NoModify", 1, RegistryValueKind.DWord);

        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);



        var displayIcon = ResolveDisplayIcon(installRoot);

        if (!string.IsNullOrWhiteSpace(displayIcon))

        {

            key.SetValue("DisplayIcon", displayIcon);

        }

    }



    private static string? ResolveDisplayIcon(string installDirectory)

    {

        var appExe = Path.Combine(installDirectory, InstallPaths.ExecutableFileName);

        if (File.Exists(appExe))

        {

            return $"{appExe},0";

        }



        var ico = Path.Combine(installDirectory, "app.ico");

        return File.Exists(ico) ? $"{ico},0" : null;

    }



    private static void TryDeleteUninstallKey(RegistryKey hive)

    {

        try

        {

            hive.DeleteSubKey(UninstallKeyPath, throwOnMissingSubKey: false);

        }

        catch

        {

            // ignore

        }

    }

}
