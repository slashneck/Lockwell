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
using System.Runtime.InteropServices;
using System.Text;

namespace Lockwell.Services;

/// <summary>
/// Detects likely screen sharing and capture tools. Shared by launch preflight
/// and the unlock gate so sharing started after preflight is still caught.
/// </summary>
public static class ScreenCaptureDetector
{
    private static readonly HashSet<string> DiscordProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "discordptb", "discordcanary",
    };

    private static readonly HashSet<string> CollaborationProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "discordptb", "discordcanary",
        "zoom", "zoommeeting",
        "ms-teams", "teams",
        "slack", "skype",
    };

    private static readonly string[] ActiveShareTitleHints =
    [
        "sharing your screen",
        "you're sharing",
        "you are sharing",
        "share your screen",
        "stop sharing",
        "screen share",
        "screen sharing",
        "is sharing",
        "presenting",
        "presenting your screen",
        "go live",
        "you are live",
        "you're live",
        "broadcasting",
        "share preview",
    ];

    private static readonly string[] DiscordShareTitleHints =
    [
        "stop sharing",
        "sharing",
        "screen share",
        "stream",
        "go live",
        "live",
        "presenting",
        "share preview",
        "voice connected",
    ];

    public static IReadOnlyList<WindowInfo> EnumerateVisibleWindows()
    {
        var windows = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return true;

            string title = ReadWindowText(hWnd);
            if (title.Length < 2) return true;

            string processName;
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { return true; }

            windows.Add(new WindowInfo(hWnd, title, processName));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static bool IsDiscordRunning()
    {
        try
        {
            return Process.GetProcesses().Any(p =>
            {
                try { return DiscordProcessNames.Contains(p.ProcessName); }
                catch { return false; }
                finally { p.Dispose(); }
            });
        }
        catch { return false; }
    }

    /// <summary>High-confidence active screen share (any app).</summary>
    public static IReadOnlyList<PreflightFinding> DetectActiveScreenShare()
    {
        var findings = new List<PreflightFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var window in EnumerateVisibleWindows())
        {
            string lower = window.Title.ToLowerInvariant();
            if (!ActiveShareTitleHints.Any(h => lower.Contains(h, StringComparison.Ordinal)))
                continue;

            string key = window.Title.ToLowerInvariant();
            if (!seen.Add(key)) continue;

            string shortTitle = window.Title.Length > 72 ? window.Title[..69] + "..." : window.Title;
            findings.Add(new PreflightFinding
            {
                ProcessName = window.ProcessName.ToLowerInvariant(),
                DisplayName = "Active screen share",
                Reason = $"Sharing may be in progress: \"{shortTitle}\". End it before unlocking.",
                Severity = ThreatSeverity.Danger,
            });
        }

        return findings;
    }

    /// <summary>Discord-specific window heuristics when Discord is running.</summary>
    public static IReadOnlyList<PreflightFinding> DetectDiscordScreenShare()
    {
        var findings = new List<PreflightFinding>();
        if (!IsDiscordRunning()) return findings;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in EnumerateVisibleWindows())
        {
            if (!DiscordProcessNames.Contains(window.ProcessName))
                continue;

            string lower = window.Title.ToLowerInvariant();
            bool matchesShare = DiscordShareTitleHints.Any(h => lower.Contains(h, StringComparison.Ordinal));
            if (!matchesShare) continue;

            string key = window.Title.ToLowerInvariant();
            if (!seen.Add(key)) continue;

            string shortTitle = window.Title.Length > 72 ? window.Title[..69] + "..." : window.Title;
            findings.Add(new PreflightFinding
            {
                ProcessName = window.ProcessName.ToLowerInvariant(),
                DisplayName = "Discord screen share",
                Reason = $"Discord may be sharing your screen: \"{shortTitle}\". Stop sharing before unlocking.",
                Severity = ThreatSeverity.Danger,
            });
        }

        return findings;
    }

    /// <summary>Risks that should gate vault unlock (active share, remote access, etc.).</summary>
    public static IReadOnlyList<PreflightFinding> ScanUnlockRisks(PreflightScanner scanner)
    {
        var merged = new List<PreflightFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRange(IEnumerable<PreflightFinding> items)
        {
            foreach (var item in items)
            {
                string key = item.DisplayName + "|" + item.Reason;
                if (seen.Add(key))
                    merged.Add(item);
            }
        }

        AddRange(scanner.Scan().Where(f => f.Severity == ThreatSeverity.Danger));
        AddRange(DetectActiveScreenShare());
        AddRange(DetectDiscordScreenShare());

        return merged
            .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool CollaborationAppIsRunning() =>
        Process.GetProcesses().Any(p =>
        {
            try { return CollaborationProcessNames.Contains(p.ProcessName); }
            catch { return false; }
            finally { p.Dispose(); }
        });

    private static string ReadWindowText(IntPtr hWnd)
    {
        int length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;

        var buffer = new StringBuilder(length + 1);
        return GetWindowText(hWnd, buffer, buffer.Capacity) > 0
            ? buffer.ToString().Trim()
            : string.Empty;
    }

    public readonly record struct WindowInfo(IntPtr Handle, string Title, string ProcessName);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
