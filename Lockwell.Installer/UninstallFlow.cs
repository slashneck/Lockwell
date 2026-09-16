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

using System.Windows;
using Lockwell.Installer.Services;

namespace Lockwell.Installer;

internal static class UninstallFlow
{
    public static void Run(bool quiet)
    {
        var installDir = InstallRegistryService.ReadInstallLocation();
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            installDir = InstallPaths.ElevatedDefaultInstallDirectory;
        }

        var removeData = false;
        if (!quiet)
        {
            var result = MessageBox.Show(
                "Remove Lockwell from this PC?\n\nChoose Yes to uninstall the app. You will be asked next about saved settings.",
                "Uninstall Lockwell",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            removeData = MessageBox.Show(
                "Also delete saved settings and sign-in data in %LocalAppData%\\Lockwell?",
                "Remove saved data",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        InstallerEngine.Uninstall(installDir, removeData);

        if (!quiet)
        {
            MessageBox.Show(
                "Lockwell was removed.",
                "Uninstall complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
