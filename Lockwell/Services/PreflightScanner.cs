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

namespace Lockwell.Services;

public enum ThreatSeverity { Info, Warning, Danger }

public sealed class PreflightFinding
{
    public required string ProcessName { get; init; }
    public required string DisplayName { get; init; }
    public required string Reason { get; init; }
    public required ThreatSeverity Severity { get; init; }
}

/// <summary>
/// Best-effort environment check before the user types a master password.
/// Combines a process watchlist, collaboration-app awareness, and window-title
/// hints for active screen sharing. Cannot detect renamed malware or a phone
/// camera pointed at the monitor.
/// </summary>
public sealed class PreflightScanner
{
    private static readonly Dictionary<string, (string display, string reason, ThreatSeverity sev)> Watchlist =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anydesk"] = ("AnyDesk", "Remote access tool can view and control your screen.", ThreatSeverity.Danger),
            ["teamviewer"] = ("TeamViewer", "Remote access tool can view and control your screen.", ThreatSeverity.Danger),
            ["rustdesk"] = ("RustDesk", "Remote access tool can view and control your screen.", ThreatSeverity.Danger),
            ["ultraviewer"] = ("UltraViewer", "Remote access tool can view and control your screen.", ThreatSeverity.Danger),
            ["aa_v3"] = ("Ammyy Admin", "Remote access tool often abused by scammers.", ThreatSeverity.Danger),
            ["winvnc"] = ("VNC Server", "Remote desktop server can stream your screen.", ThreatSeverity.Danger),
            ["tvnserver"] = ("TightVNC", "Remote desktop server can stream your screen.", ThreatSeverity.Danger),
            ["vncserver"] = ("VNC Server", "Remote desktop server can stream your screen.", ThreatSeverity.Danger),
            ["parsec"] = ("Parsec", "Remote desktop streaming is active or available.", ThreatSeverity.Danger),
            ["obs64"] = ("OBS Studio", "Screen recorder is running and may capture the screen.", ThreatSeverity.Warning),
            ["obs32"] = ("OBS Studio", "Screen recorder is running and may capture the screen.", ThreatSeverity.Warning),
            ["bandicam"] = ("Bandicam", "Screen recorder is running and may capture the screen.", ThreatSeverity.Warning),
            ["camtasia"] = ("Camtasia", "Screen recorder is running and may capture the screen.", ThreatSeverity.Warning),
            ["camtasiastudio"] = ("Camtasia", "Screen recorder is running and may capture the screen.", ThreatSeverity.Warning),
            ["action"] = ("Action! Recorder", "Screen recorder is running and may capture the screen.", ThreatSeverity.Warning),
            ["fraps"] = ("Fraps", "Screen recorder is running and may capture the screen.", ThreatSeverity.Warning),
            ["sharex"] = ("ShareX", "Screen capture tool is running.", ThreatSeverity.Warning),
            ["snippingtool"] = ("Snipping Tool", "Screenshot tool is running.", ThreatSeverity.Info),
            ["screenrec"] = ("ScreenRec", "Screen recorder is running.", ThreatSeverity.Warning),
            ["mstsc"] = ("Remote Desktop", "An active Remote Desktop session may be viewing this PC.", ThreatSeverity.Warning),
            ["discord"] = ("Discord", "Voice chat app can screen-share your desktop when you are in a call.", ThreatSeverity.Warning),
            ["discordptb"] = ("Discord PTB", "Voice chat app can screen-share your desktop when you are in a call.", ThreatSeverity.Warning),
            ["discordcanary"] = ("Discord Canary", "Voice chat app can screen-share your desktop when you are in a call.", ThreatSeverity.Warning),
            ["zoom"] = ("Zoom", "Meeting app can share your screen to other participants.", ThreatSeverity.Warning),
            ["zoommeeting"] = ("Zoom Meeting", "Meeting app can share your screen to other participants.", ThreatSeverity.Warning),
            ["ms-teams"] = ("Microsoft Teams", "Meeting app can share your screen to other participants.", ThreatSeverity.Warning),
            ["teams"] = ("Microsoft Teams", "Meeting app can share your screen to other participants.", ThreatSeverity.Warning),
            ["slack"] = ("Slack", "Chat app supports screen sharing during calls.", ThreatSeverity.Warning),
            ["skype"] = ("Skype", "Chat app can share your screen during calls.", ThreatSeverity.Warning),
        };

    public IReadOnlyList<PreflightFinding> Scan()
    {
        var findings = new List<PreflightFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return findings; }

        try
        {
            foreach (var proc in processes)
            {
                string name;
                try { name = proc.ProcessName; }
                catch { continue; }

                string key = name.ToLowerInvariant();
                if (Watchlist.TryGetValue(key, out var info) && seen.Add("proc:" + key))
                {
                    findings.Add(new PreflightFinding
                    {
                        ProcessName = key,
                        DisplayName = info.display,
                        Reason = info.reason,
                        Severity = info.sev,
                    });
                }
            }

            AddUnique(findings, seen, ScreenCaptureDetector.DetectActiveScreenShare());
            AddUnique(findings, seen, ScreenCaptureDetector.DetectDiscordScreenShare());
        }
        finally
        {
            foreach (var proc in processes)
                proc.Dispose();
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<PreflightFinding> ScanUnlockRisks() =>
        ScreenCaptureDetector.ScanUnlockRisks(this);

    private static void AddUnique(
        List<PreflightFinding> findings,
        HashSet<string> seen,
        IEnumerable<PreflightFinding> items)
    {
        foreach (var item in items)
        {
            string key = item.DisplayName + "|" + item.Reason;
            if (seen.Add(key))
                findings.Add(item);
        }
    }
}
