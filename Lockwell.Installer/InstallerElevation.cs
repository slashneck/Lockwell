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

using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace Lockwell.Installer;

internal static class InstallerElevation
{
    private const string ElevatedFlag = "--elevated";

    public static bool EnsureElevated(IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (IsProcessElevated() || args.Any(IsElevatedFlag))
        {
            return true;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            MessageBox.Show(
                "Could not locate LockwellSetup.exe to request administrator access.",
                "Lockwell Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        try
        {
            var relaunchArgs = string.Join(" ",
                args.Where(a => !IsElevatedFlag(a)).Append(ElevatedFlag));

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = relaunchArgs,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch
        {
            MessageBox.Show(
                "Lockwell Setup needs administrator approval to install under Program Files.",
                "Lockwell Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return false;
    }

    private static bool IsElevatedFlag(string arg) =>
        string.Equals(arg, ElevatedFlag, StringComparison.OrdinalIgnoreCase);

    internal static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
