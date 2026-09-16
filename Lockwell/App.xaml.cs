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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lockwell.Services;
namespace Lockwell;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                CrashLog.Write(ex);
        };
        DispatcherUnhandledException += OnUnhandledException;

        if (IsSmokeTestLaunch(e.Args))
        {
            RunSmokeTest();
            Shutdown();
            return;
        }

        // Live UI preview. Same throwaway vault as the screenshot harness, but the window
        // is left open so it can be captured from the screen instead of rendered to a
        // bitmap. RenderTargetBitmap composites translucent surfaces differently from the
        // real compositor, so a design judged only on those captures is judged on a
        // picture the app never actually draws.
        int previewAt = Array.FindIndex(e.Args,
            a => string.Equals(a, "--ui-preview", StringComparison.OrdinalIgnoreCase));
        if (previewAt >= 0)
        {
            string previewSandbox = Path.Combine(
                Path.GetTempPath(), "lockwell-preview-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("LOCKWELL_USER_DATA", previewSandbox);

            string screen = previewAt + 1 < e.Args.Length ? e.Args[previewAt + 1] : "home";
            RunUiPreview(screen);
            return;
        }

        // Screenshot harness. Runs against a throwaway vault in a temp folder: the
        // redirect is set here, before anything has had a chance to read the real one.
        int shotsAt = Array.FindIndex(e.Args,
            a => string.Equals(a, "--store-shots", StringComparison.OrdinalIgnoreCase));
        if (shotsAt >= 0)
        {
            string outDir = shotsAt + 1 < e.Args.Length
                ? e.Args[shotsAt + 1]
                : Path.Combine(Environment.CurrentDirectory, "store");

            string sandbox = Path.Combine(
                Path.GetTempPath(), "lockwell-store-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("LOCKWELL_USER_DATA", sandbox);

            RunStoreShots(outDir, sandbox);
            return;
        }

        // Before anything can be unlocked: keep an unlocked vault out of crash dumps.
        // A dump is a copy of process memory, so for an open vault it would contain the
        // data key and whatever was decrypted at the time, written to disk and outliving
        // the process entirely.
        CrashExposure.Apply();

        // Clear scratch files left by a crash or power loss. Without this, decrypted
        // media could sit in %TEMP% until the next time the vault happened to lock.
        VaultTempFiles.PurgeAll();

        base.OnStartup(e);
    }

    /// <summary>
    /// Open the real shell against a demo vault and leave it on screen. Used only for
    /// looking at the design as the compositor actually draws it.
    /// </summary>
    private async void RunUiPreview(string screen)
    {
        try
        {
            await Lockwell.Automation.StoreShots.ShowPreviewAsync(screen);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[preview] failed: " + ex);
            Shutdown(1);
        }
    }

    private async void RunStoreShots(string outDir, string sandbox)
    {
        int code = 0;
        try
        {
            await Lockwell.Automation.StoreShots.RunAsync(outDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[store] failed: " + ex);
            code = 1;
        }
        finally
        {
            // The demo vault exists only for this run.
            try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
            catch { /* temp cleanup is best effort */ }
        }

        Environment.ExitCode = code;
        Shutdown(code);
    }

    private static bool IsSmokeTestLaunch(string[] args) =>
        args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));

    private static void RunSmokeTest()
    {
        var settings = AppSettings.Load();
        var profiles = new ProfileManager(settings);
        profiles.EnsureInitialized();
        if (!File.Exists(AppSettings.FilePath))
            throw new InvalidOperationException("settings.json was not created during smoke test.");
        Environment.ExitCode = 0;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        VaultTempFiles.PurgeAll();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = CrashLog.Write(e.Exception);
        var message = logPath is not null
            ? "Lockwell hit an unexpected error and will close to keep your vault safe.\n\nDetails were saved to:\n" + logPath
            : "Lockwell hit an unexpected error and will close to keep your vault safe.";

        MessageBox.Show(
            message,
            "Lockwell",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
        Shutdown();
    }
}
