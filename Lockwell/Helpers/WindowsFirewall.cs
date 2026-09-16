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

using System.Collections;
using System.Diagnostics;
using Lockwell.Sync;

namespace Lockwell.Helpers;

/// <summary>How Windows Firewall currently treats incoming connections to this app.</summary>
public enum FirewallState
{
    /// <summary>The firewall could not be queried. Say nothing rather than guess.</summary>
    Unknown,

    /// <summary>A rule explicitly permits the sync port. Linking should work.</summary>
    Allowed,

    /// <summary>
    /// A rule explicitly blocks this program. This is what Windows writes when someone
    /// answers "Cancel" to the "allow this app?" prompt, and it is permanent and silent.
    /// </summary>
    Blocked,

    /// <summary>
    /// Nothing mentions this program at all. Windows denies unsolicited incoming
    /// connections by default, so this fails exactly like a block, just less visibly.
    /// </summary>
    NotAllowed,
}

public sealed record FirewallVerdict(FirewallState State, string Detail)
{
    /// <summary>True when a phone trying to connect is dropped before Lockwell ever sees it.</summary>
    public bool WouldDropIncoming => State is FirewallState.Blocked or FirewallState.NotAllowed;
}

/// <summary>
/// Why this exists.
///
/// A phone that cannot reach the PC times out, and a timeout looks identical whether the
/// address was wrong, the two devices are on different networks, or Windows quietly
/// dropped the connection. That ambiguity sent a real user hunting for a network problem
/// that did not exist: the actual cause was two "Query User" block rules, written when the
/// Windows Firewall prompt was dismissed with Cancel once, earlier in the app's life.
///
/// Those rules never announce themselves and never expire. So Lockwell checks before it
/// starts waiting, and says plainly what is wrong instead of letting the phone report a
/// network fault. Reading firewall policy needs no elevation; changing it does, which is
/// why the repair is offered as an explicit action, never applied behind the user's back.
/// </summary>
public static class WindowsFirewall
{
    /// <summary>
    /// The one rule Lockwell ever creates. Named as a constant so that creating it,
    /// finding it again and removing it can never drift apart, and so a user reading
    /// their firewall list can tell at a glance which entry belongs to this app.
    /// </summary>
    public const string RuleName = "Lockwell device linking";

    private const int DirectionInbound = 1;
    private const int ActionBlock = 0;
    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int PublicProfile = 4;

    /// <summary>This app's own executable, which is what firewall rules are written against.</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? "";

    /// <summary>
    /// Inspect the rules that apply to the network this PC is on right now. Enumerating
    /// firewall policy is a COM call across hundreds of rules, so callers should keep this
    /// off the UI thread.
    /// </summary>
    public static FirewallVerdict Inspect()
    {
        try
        {
            dynamic? policy = CreatePolicy();
            if (policy is null) return new FirewallVerdict(FirewallState.Unknown, "");

            int activeProfiles = (int)policy.CurrentProfileTypes;
            string self = ExecutablePath;

            bool sawAllow = false;
            string blockedBy = "";

            foreach (object entry in (IEnumerable)policy.Rules)
            {
                dynamic rule = entry;

                string name, application, ports;
                int direction, action, profiles, protocol;
                bool enabled;

                try
                {
                    enabled = (bool)rule.Enabled;
                    direction = (int)rule.Direction;
                    action = (int)rule.Action;
                    profiles = (int)rule.Profiles;
                    protocol = (int)rule.Protocol;
                    name = (string?)rule.Name ?? "";
                    application = (string?)rule.ApplicationName ?? "";
                    ports = (string?)rule.LocalPorts ?? "";
                }
                catch
                {
                    // Some rules refuse to describe themselves. Skipping one is fine;
                    // failing the whole check because of one is not.
                    continue;
                }

                if (!enabled) continue;
                if (direction != DirectionInbound) continue;

                // A rule scoped to a profile this PC is not currently on has no effect.
                if ((profiles & activeProfiles) == 0 && profiles != int.MaxValue) continue;

                bool matchesThisApp =
                    application.Length > 0 && self.Length > 0 &&
                    string.Equals(application, self, StringComparison.OrdinalIgnoreCase);

                bool matchesSyncPort =
                    (protocol == ProtocolTcp && MentionsPort(ports, SyncProtocol.DefaultPort)) ||
                    (protocol == ProtocolUdp && MentionsPort(ports, LocalDiscovery.DiscoveryPort));

                if (!matchesThisApp && !matchesSyncPort) continue;

                if (action == ActionBlock)
                {
                    // Block beats allow in Windows Firewall, so one is enough to decide.
                    blockedBy = name;
                    break;
                }

                sawAllow = true;
            }

            if (blockedBy.Length > 0)
                return new FirewallVerdict(FirewallState.Blocked, blockedBy);

            return sawAllow
                ? new FirewallVerdict(FirewallState.Allowed, "")
                : new FirewallVerdict(FirewallState.NotAllowed, "");
        }
        catch
        {
            return new FirewallVerdict(FirewallState.Unknown, "");
        }
    }

    /// <summary>
    /// Whether the rule Lockwell adds is currently present. Distinct from
    /// <see cref="Inspect"/>: incoming connections can be permitted by something else
    /// entirely, and in that case there is nothing of ours to take away again.
    /// </summary>
    public static bool OwnRuleExists()
    {
        try
        {
            dynamic? policy = CreatePolicy();
            if (policy is null) return false;

            foreach (object entry in (IEnumerable)policy.Rules)
            {
                dynamic rule = entry;
                try
                {
                    if ((string?)rule.Name == RuleName) return true;
                }
                catch { continue; }
            }
        }
        catch { /* fall through to false */ }

        return false;
    }

    /// <summary>
    /// Undo exactly what <see cref="TryRepairElevated"/> added, and nothing else. A
    /// security app that opens a port should be able to close it again from the same
    /// screen; leaving the user to find it in Windows Defender Firewall would be a
    /// poor answer for a feature the app itself turned on.
    /// </summary>
    public static string RevertScript() =>
        "Get-NetFirewallRule -DisplayName '" + RuleName +
        "' -ErrorAction SilentlyContinue | Remove-NetFirewallRule";

    public static bool TryRevertElevated() => RunElevated(RevertScript());

    /// <summary>
    /// Whether Windows has this network marked public. Public networks deny incoming
    /// connections by default, which is right in a cafe and wrong at home.
    /// </summary>
    public static bool OnPublicNetwork()
    {
        try
        {
            dynamic? policy = CreatePolicy();
            if (policy is null) return false;
            return ((int)policy.CurrentProfileTypes & PublicProfile) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The exact commands that fix it. The rule created is as narrow as the job allows:
    /// one TCP port, incoming only, and only from the local subnet, so nothing beyond
    /// this network gains anything from it.
    /// </summary>
    public static string RepairScript()
    {
        string exe = ExecutablePath.Replace("'", "''");
        return
            // Clear the stale blocks first: a block beats an allow, so adding one without
            // removing these would change nothing.
            "Get-NetFirewallRule | Where-Object { ($_ | Get-NetFirewallApplicationFilter).Program -eq '"
                + exe + "' -and $_.Action -eq 'Block' } | Remove-NetFirewallRule -ErrorAction SilentlyContinue"
            + Environment.NewLine
            // Re-running this should not pile up duplicates of our own rule.
            + "Get-NetFirewallRule -DisplayName '" + RuleName
                + "' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue"
            + Environment.NewLine
            // The transfer itself.
            + "New-NetFirewallRule -DisplayName '" + RuleName + "' -Direction Inbound -Action Allow "
            + "-Protocol TCP -LocalPort " + SyncProtocol.DefaultPort + " -RemoteAddress LocalSubnet"
            + Environment.NewLine
            // Letting the phone find this PC at all, so nobody has to type an address.
            + "New-NetFirewallRule -DisplayName '" + RuleName + "' -Direction Inbound -Action Allow "
            + "-Protocol UDP -LocalPort " + LocalDiscovery.DiscoveryPort + " -RemoteAddress LocalSubnet";
    }

    /// <summary>
    /// Hand the repair to an elevated PowerShell. Windows shows its own consent prompt.
    /// Declining it changes nothing, and is reported as "not applied" rather than an error.
    /// </summary>
    public static bool TryRepairElevated() => RunElevated(RepairScript());

    private static bool RunElevated(string script)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command "
                            + Quote(script.Replace(Environment.NewLine, "; ")),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            // Declining the elevation prompt lands here. That is a choice, not a failure.
            return false;
        }
    }

    private static object? CreatePolicy()
    {
        Type? type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        return type is null ? null : Activator.CreateInstance(type);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static bool MentionsPort(string localPorts, int port)
    {
        if (localPorts.Length == 0) return false;
        if (localPorts == "*") return true;

        string wanted = port.ToString();
        foreach (string part in localPorts.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part == wanted) return true;

            int dash = part.IndexOf('-');
            if (dash <= 0) continue;
            if (int.TryParse(part[..dash], out int lo) &&
                int.TryParse(part[(dash + 1)..], out int hi) &&
                port >= lo && port <= hi)
            {
                return true;
            }
        }
        return false;
    }
}
