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

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Lockwell.Sync;

/// <summary>
/// One IPv4 address this device holds on a local network, with enough context for a
/// human to recognise which one is which.
/// </summary>
public sealed record LocalAddress(
    string Address,
    string InterfaceName,
    bool IsWireless,
    bool IsVirtual,
    bool IsPrivateRange,
    int Rank,
    bool IsUsb = false)
{
    /// <summary>Short human label for the adapter kind, for a UI to show beside the address.</summary>
    public string KindLabel =>
        IsUsb ? "USB cable" : IsVirtual ? "Virtual" : IsWireless ? "Wi-Fi" : "Wired";
}

/// <summary>
/// A network adapter as the OS describes it. Split out from the live lookup so the
/// selection rules can be tested against fixed input instead of whatever adapters
/// happen to exist on the machine running the tests.
/// </summary>
public readonly record struct AddressCandidate(
    string Address,
    string InterfaceName,
    string Description,
    NetworkInterfaceType Kind,
    OperationalStatus Status);

/// <summary>
/// Which addresses a device can be reached at on the local network.
///
/// This replaces an earlier "best guess" that opened a UDP socket toward a public IP
/// and reported whichever interface the OS picked for it. On a machine with more than
/// one active adapter -- a wired LAN and Wi-Fi at the same time is the common case --
/// that guess is a coin flip, and the wrong half of the time it tells the user an
/// address their phone cannot reach. The listener binds <see cref="IPAddress.Any"/>
/// and accepts traffic arriving on any interface, so the guess was never a limit on
/// what worked, only on what the user was told.
///
/// So: enumerate every candidate, rank them by how likely a phone on the same network
/// is to reach them, and let the user see all of them rather than picking one for them.
/// </summary>
public static class LocalNetwork
{
    /// <summary>
    /// Substrings that mark an adapter as virtual: a hypervisor switch, a VPN tunnel, a
    /// container bridge. Matching on names is a heuristic, so it only ever affects the
    /// order addresses appear in -- nothing is hidden because of it.
    /// </summary>
    /// <summary>
    /// How a phone tethered over a cable shows up. Windows presents USB tethering as an
    /// ordinary Ethernet adapter, so only the description distinguishes it -- and it is
    /// worth distinguishing, because it is the best link there is: a direct cable between
    /// the two devices, with no Wi-Fi and no router in between.
    /// </summary>
    private static readonly string[] UsbMarkers =
    {
        "remote ndis", "rndis", "usb ethernet", "usb networking", "android usb",
        "usb rndis", "ncm", "tethering",
    };

    private static readonly string[] VirtualMarkers =
    {
        "hyper-v", "vethernet", "vmware", "virtualbox", "vbox", "wsl", "docker",
        "tailscale", "zerotier", "wireguard", "openvpn", "tap-", "tap adapter",
        "pseudo-interface", "loopback", "npcap", "bluetooth", "vpn", "virtual",
    };

    /// <summary>Every usable IPv4 address on this machine, best candidate first.</summary>
    public static IReadOnlyList<LocalAddress> Detect()
    {
        var candidates = new List<AddressCandidate>();

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch { continue; }

                foreach (UnicastIPAddressInformation info in props.UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    candidates.Add(new AddressCandidate(
                        info.Address.ToString(),
                        nic.Name,
                        nic.Description,
                        nic.NetworkInterfaceType,
                        nic.OperationalStatus));
                }
            }
        }
        catch
        {
            // A machine that will not describe its own adapters gets an empty list
            // rather than a wrong answer. The UI says "ask your network" in that case.
            return Array.Empty<LocalAddress>();
        }

        return Select(candidates);
    }

    /// <summary>
    /// Filter and rank candidates. Pure: the same input always gives the same output,
    /// which is what makes the rules testable.
    /// </summary>
    public static IReadOnlyList<LocalAddress> Select(IEnumerable<AddressCandidate> candidates)
    {
        var chosen = new List<LocalAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (AddressCandidate c in candidates)
        {
            if (c.Status != OperationalStatus.Up) continue;
            if (c.Kind is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            if (!IPAddress.TryParse(c.Address, out IPAddress? ip)) continue;
            if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
            if (IPAddress.IsLoopback(ip)) continue;

            // The same address can appear twice if an adapter reports it more than once.
            if (!seen.Add(c.Address)) continue;

            byte[] octets = ip.GetAddressBytes();
            bool wireless = c.Kind == NetworkInterfaceType.Wireless80211;
            bool usb = Matches(c.InterfaceName, UsbMarkers) || Matches(c.Description, UsbMarkers);

            // A tethered phone must never be mistaken for a hypervisor's adapter and
            // buried at the bottom of the list; it is the most direct path available.
            bool virt = !usb && (LooksVirtual(c.InterfaceName) || LooksVirtual(c.Description));
            bool physical = usb || (!virt && c.Kind is NetworkInterfaceType.Ethernet
                                              or NetworkInterfaceType.Wireless80211
                                              or NetworkInterfaceType.GigabitEthernet
                                              or NetworkInterfaceType.FastEthernetT
                                              or NetworkInterfaceType.FastEthernetFx);
            bool selfAssigned = octets[0] == 169 && octets[1] == 254;
            bool priv = IsPrivate(octets);

            // Lower ranks are offered first. A real adapter holding a home-network
            // address is what a phone on the same Wi-Fi almost always needs.
            int rank = (selfAssigned, physical, priv) switch
            {
                (true, _, _) => 4,      // no DHCP answered; rarely what anyone wants
                (_, true, true) => 0,   // physical adapter, private range
                (_, true, false) => 1,  // physical adapter, routable or carrier-grade NAT
                (_, false, true) => 2,  // virtual adapter, private range
                _ => 3,
            };

            chosen.Add(new LocalAddress(c.Address, c.InterfaceName, wireless, virt, priv, rank, usb));
        }

        // Wireless first inside a tier: the phone is on Wi-Fi by definition, so if this
        // machine also has Wi-Fi that is the likeliest shared network. Name is the final
        // tiebreak purely so the order never shuffles between openings of the dialog.
        return chosen
            .OrderBy(a => a.Rank)
            // A cable first: it is direct, it does not depend on the Wi-Fi working, and
            // someone who plugged one in almost certainly meant to use it.
            .ThenByDescending(a => a.IsUsb)
            .ThenByDescending(a => a.IsWireless)
            .ThenBy(a => a.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Address, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The RFC 1918 ranges a home or office router hands out.</summary>
    private static bool IsPrivate(byte[] o) =>
        o[0] == 10 ||
        (o[0] == 172 && o[1] >= 16 && o[1] <= 31) ||
        (o[0] == 192 && o[1] == 168);

    private static bool LooksVirtual(string text) => Matches(text, VirtualMarkers);

    private static bool Matches(string text, string[] markers)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (string marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
