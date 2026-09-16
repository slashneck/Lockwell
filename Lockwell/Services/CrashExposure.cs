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

namespace Lockwell.Services;

/// <summary>
/// Keeps the vault's secrets out of crash dumps and error reports.
///
/// When a Windows program crashes, Windows Error Reporting can write a dump of the
/// process to disk and offer to send it to Microsoft. A dump is a copy of the process's
/// memory, so for an unlocked vault it contains the data key, decrypted entries, and
/// whatever media happened to be decoded at that moment. That file then sits in
/// %LOCALAPPDATA%\CrashDumps outliving the process entirely, which is precisely the
/// situation <see cref="Lockwell.Crypto.LockedKey"/> exists to prevent for the page file.
///
/// Two things are done about it, both cheap and neither sufficient alone:
///
///   - This process is excluded from Windows Error Reporting, so WER does not collect or
///     upload a report for it.
///   - The default error mode is changed so a crash fails quietly rather than going
///     through the dialog that offers to send details.
///
/// The honest limit, and it is a real one: this asks Windows not to collect a dump. It
/// does not prevent someone attaching a debugger, and it does not stop a dump requested
/// deliberately by another tool running as the same user. Anyone in that position can
/// already read the process's memory directly. What this closes is the accidental case,
/// where an ordinary crash quietly leaves a copy of an unlocked vault on the disk.
/// </summary>
public static class CrashExposure
{
    [DllImport("wer.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WerAddExcludedApplication(
        string pwzExeName, [MarshalAs(UnmanagedType.Bool)] bool bAllUsers);

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    /// <summary>Whether Windows agreed to leave this program out of error reporting.</summary>
    public static bool ExcludedFromReporting { get; private set; }

    /// <summary>
    /// Apply both measures. Called once at startup, before the vault can be unlocked, so
    /// there is never a window where an unlocked vault could be dumped.
    /// </summary>
    public static void Apply()
    {
        try
        {
            // Suppress the crash dialog and the "send details?" flow that goes with it.
            SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        }
        catch
        {
            // An older or restricted Windows simply keeps its defaults.
        }

        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            // Per-user, so this needs no administrator rights. Asking for all users would
            // need elevation, which this app deliberately never takes.
            int hr = WerAddExcludedApplication(Path.GetFileName(exe), bAllUsers: false);
            ExcludedFromReporting = hr == 0;
        }
        catch
        {
            ExcludedFromReporting = false;
        }
    }
}
