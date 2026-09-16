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

namespace Lockwell.Installer;

using Lockwell.Installer.Services;

internal static class InstallerEngine
{
    public sealed record InstallPlan(
        string InstallDirectory,
        bool CreateDesktopShortcut,
        bool CreateStartMenuShortcut,
        string ReleaseTag,
        Version ReleaseVersion,
        string DownloadUrl);

    public static Task<GitHubReleaseClient.ReleaseCheckResult?> FetchLatestReleaseAsync(CancellationToken ct = default) =>
        GitHubReleaseClient.CheckLatestReleaseAsync(ct);

    public static Task<string?> DownloadReleaseZipAsync(
        string downloadUrl,
        IProgress<double>? progress,
        CancellationToken ct = default) =>
        GitHubReleaseClient.DownloadReleaseZipAsync(downloadUrl, progress, ct);

    public static void ApplyShortcuts(InstallPlan plan)
    {
        var exe = Path.Combine(plan.InstallDirectory, InstallPaths.ExecutableFileName);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("Lockwell.exe was not found after install.", exe);
        }

        var icon = ShortcutService.ResolveIconLocationNextToExe(exe);
        if (plan.CreateDesktopShortcut)
        {
            ShortcutService.CreateOrUpdateOnDesktop(InstallPaths.InstallFolderName, exe, icon);
        }

        if (plan.CreateStartMenuShortcut)
        {
            ShortcutService.CreateOrUpdateInStartMenu(InstallPaths.InstallFolderName, exe, icon);
        }
    }

    public static void Uninstall(string installDirectory, bool removeUserData)
    {
        ShortcutService.RemoveDesktopShortcut(InstallPaths.InstallFolderName);
        ShortcutService.RemoveStartMenuShortcut(InstallPaths.InstallFolderName);

        try
        {
            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, recursive: true);
            }
        }
        catch
        {
            // ignore partial delete
        }

        if (removeUserData)
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Lockwell");
            try
            {
                if (Directory.Exists(appData))
                {
                    Directory.Delete(appData, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
        }

        InstallRegistryService.UnregisterInstall();
    }
}
