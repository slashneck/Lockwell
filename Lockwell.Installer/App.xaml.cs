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
using System.Windows.Threading;

namespace Lockwell.Installer;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var args = e.Args ?? [];

        if (UpdateFlow.IsUpdateMode(args))
        {
            if (!InstallerElevation.EnsureElevated(args))
            {
                Shutdown();
                return;
            }

            var patchWindow = new MainWindow(startPatchMode: true, updateArgs: args);
            MainWindow = patchWindow;
            patchWindow.Show();
            return;
        }

        if (args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(a, "-uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            if (!InstallerElevation.EnsureElevated(args))
            {
                Shutdown();
                return;
            }

            var quiet = args.Any(a => string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase));
            UninstallFlow.Run(quiet);
            Shutdown();
            return;
        }

        if (!InstallerElevation.EnsureElevated(args))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "Lockwell Setup",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
