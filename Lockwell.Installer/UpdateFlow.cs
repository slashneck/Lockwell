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
using System.IO;
using System.Windows;
using Lockwell.Installer.Services;
using Lockwell.Update;

namespace Lockwell.Installer;

internal static class UpdateFlow
{
    public static bool IsUpdateMode(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, "/update", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(a, "-update", StringComparison.OrdinalIgnoreCase));

    public static UpdateRequest ParseRequest(string[] args)
    {
        var zip = ReadArgValue(args, "--zip=");
        var manifest = ReadArgValue(args, "--manifest=");
        var signature = ReadArgValue(args, "--signature=");
        var pid = ReadArgInt(args, "--pid=");
        var version = ReadArgVersion(args, "--version=");
        var noRestart = HasFlag(args, "/norestart") || HasFlag(args, "-norestart");
        return new UpdateRequest(zip, manifest, signature, pid, version, noRestart);
    }

    internal static async Task ExecuteAsync(UpdateRequest request, IProgress<(string Status, double Progress)> progress, CancellationToken ct)
    {
        progress.Report(("Locating install…", 0.05));
        var installDir = ResolveInstallDirectory();
        if (string.IsNullOrWhiteSpace(installDir))
        {
            throw new InvalidOperationException("Could not find an existing Lockwell install.");
        }

        progress.Report(("Waiting for Lockwell to close…", 0.12));
        await WaitForProcessExitAsync(request.WaitPid, ct).ConfigureAwait(true);
        EnsureLockwellNotRunning();

        string? zipPath = request.ZipPath;
        Version targetVersion = request.TargetVersion ?? GitHubReleaseClient.ReadAppVersion() ?? new Version(1, 0, 0);
        string? tag = null;

        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            progress.Report(("Checking for the latest release…", 0.2));
            var release = await GitHubReleaseClient.CheckLatestReleaseAsync(ct).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Could not reach the update server or no release package was found.");

            targetVersion = release.LatestVersion;
            tag = release.TagName;

            progress.Report(("Downloading update…", 0.25));
            zipPath = await GitHubReleaseClient.DownloadReleaseZipAsync(
                release.DownloadUrl,
                new Progress<double>(p => progress.Report(("Downloading update…", 0.25 + (p / 100.0) * 0.45))),
                ct).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            {
                throw new InvalidOperationException("Download failed.");
            }
        }

        progress.Report(("Verifying signature…", 0.72));
        VerifyOrRefuse(request, zipPath);

        progress.Report(("Installing files…", 0.78));
        if (!GitHubReleaseClient.InstallReleaseZipToDirectory(zipPath, installDir))
        {
            throw new InvalidOperationException("Could not apply the update package.");
        }

        progress.Report(("Registering update…", 0.9));
        var uninstallExe = InstallRegistryService.CopyUninstallerToInstallDir(installDir);
        InstallRegistryService.RegisterInstall(installDir, targetVersion, uninstallExe);

        try
        {
            File.Delete(zipPath);
        }
        catch
        {
            // ignore temp cleanup
        }

        progress.Report(($"Updated to v{targetVersion}" + (tag is null ? "" : $" ({tag})"), 1));

        if (!request.NoRestart)
        {
            var appExe = Path.Combine(installDir, InstallPaths.ExecutableFileName);
            if (File.Exists(appExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = appExe,
                    UseShellExecute = true,
                    WorkingDirectory = installDir
                });
            }
        }
    }

    /// <summary>
    /// Refuse to write anything into the install directory unless the package verifies
    /// against a manifest signed by Lockwell's release key.
    ///
    /// This runs even though the app that launched us already checked. That is not
    /// redundancy for its own sake: this process is the one that replaces files in Program
    /// Files, it accepts a path on its command line, and it runs elevated. Trusting the
    /// caller would mean anything able to start this executable could hand it a package of
    /// its choosing and have it installed with administrator rights. The check belongs
    /// where the writing happens.
    ///
    /// A build with no signing key compiled in cannot verify and therefore does not
    /// install. That is deliberate: an updater that cannot tell a real release from a
    /// forged one is worse than no updater.
    /// </summary>
    private static void VerifyOrRefuse(UpdateRequest request, string zipPath)
    {
        if (string.IsNullOrWhiteSpace(request.ManifestPath) || string.IsNullOrWhiteSpace(request.SignaturePath))
        {
            throw new InvalidOperationException(
                "This update package did not come with a signed manifest, so it cannot be "
                + "verified. Nothing was installed. Download the installer from the "
                + "releases page instead.");
        }

        if (!File.Exists(request.ManifestPath) || !File.Exists(request.SignaturePath))
        {
            throw new InvalidOperationException(
                "The signed manifest for this update could not be found. Nothing was installed.");
        }

        byte[] manifestBytes = File.ReadAllBytes(request.ManifestPath);
        byte[] signature = File.ReadAllBytes(request.SignaturePath);

        var verdict = ReleaseTrust.VerifyPackage(manifestBytes, signature, zipPath);
        if (!verdict.IsTrusted)
        {
            throw new InvalidOperationException(verdict.Explain());
        }
    }

    public static string? ResolveInstallDirectory()
    {
        var registered = InstallRegistryService.ReadInstallLocation();
        if (!string.IsNullOrWhiteSpace(registered))
        {
            var exe = Path.Combine(registered.TrimEnd('\\'), InstallPaths.ExecutableFileName);
            if (File.Exists(exe))
            {
                return Path.GetFullPath(registered.TrimEnd('\\', '/'));
            }
        }

        foreach (var candidate in new[] { InstallPaths.ElevatedDefaultInstallDirectory, InstallPaths.LegacyDefaultInstallDirectory })
        {
            var exe = Path.Combine(candidate, InstallPaths.ExecutableFileName);
            if (File.Exists(exe))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static async Task WaitForProcessExitAsync(int? pid, CancellationToken ct)
    {
        if (pid is not int p || p <= 0)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var proc = Process.GetProcessById(p);
                if (proc.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static void EnsureLockwellNotRunning()
    {
        foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstallPaths.ExecutableFileName)))
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static string? ReadArgValue(IReadOnlyList<string> args, string prefix)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = arg[prefix.Length..].Trim();
            if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
            {
                value = value[1..^1];
            }

            return value;
        }

        return null;
    }

    private static int? ReadArgInt(IReadOnlyList<string> args, string prefix)
    {
        var value = ReadArgValue(args, prefix);
        return int.TryParse(value, out var n) ? n : null;
    }

    private static Version? ReadArgVersion(IReadOnlyList<string> args, string prefix)
    {
        var value = ReadArgValue(args, prefix);
        return Version.TryParse(value, out var v) ? v : null;
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    public sealed record UpdateRequest(
        string? ZipPath,
        string? ManifestPath,
        string? SignaturePath,
        int? WaitPid,
        Version? TargetVersion,
        bool NoRestart);
}
