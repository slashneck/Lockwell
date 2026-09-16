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

using System.Net.NetworkInformation;
using Lockwell.Sync;

namespace Lockwell.Tests;

/// <summary>
/// Which address the user gets told to type on the phone.
///
/// This exists because of a real bug: the old code opened a UDP socket toward a public
/// address and reported whichever adapter the OS picked. On a PC with a wired LAN and
/// Wi-Fi up at the same time it reported one of them arbitrarily, and told the user
/// "no device on the same network" when the phone was in fact perfectly reachable on
/// the other one. The rules below are what replaced the guess, so they are worth
/// pinning down: the wired-and-wireless case in particular.
/// </summary>
internal static class NetworkChecks
{
    public static void Run()
    {
        Selection();
        Ranking();
        LiveMachine();
    }

    private static AddressCandidate Up(
        string address, string name, string description, NetworkInterfaceType kind) =>
        new(address, name, description, kind, OperationalStatus.Up);

    private static void Selection()
    {
        Check.Section("37. Choosing which local address to show");

        // The exact shape of the machine that hit the bug.
        var both = LocalNetwork.Select(new[]
        {
            Up("192.168.1.40", "Ethernet", "Realtek Gaming GbE Family Controller",
                NetworkInterfaceType.Ethernet),
            Up("192.168.1.77", "Wi-Fi", "Intel(R) Wi-Fi 6 AX200 160MHz",
                NetworkInterfaceType.Wireless80211),
        });

        Check.That("a wired and wireless PC offers both addresses, not one", both.Count == 2);
        Check.That("neither address is silently dropped",
            both.Any(a => a.Address == "192.168.1.40") &&
            both.Any(a => a.Address == "192.168.1.77"));

        // Loopback is never something a phone can reach.
        var withLoopback = LocalNetwork.Select(new[]
        {
            Up("127.0.0.1", "Loopback Pseudo-Interface 1", "Software Loopback",
                NetworkInterfaceType.Loopback),
            Up("192.168.1.77", "Wi-Fi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211),
        });
        Check.That("loopback is never offered", withLoopback.All(a => a.Address != "127.0.0.1"));
        Check.That("the real address survives beside it", withLoopback.Count == 1);

        // An adapter that is down is not a route to anything.
        var withDown = LocalNetwork.Select(new[]
        {
            new AddressCandidate("192.168.1.40", "Ethernet", "Realtek GbE",
                NetworkInterfaceType.Ethernet, OperationalStatus.Down),
            Up("192.168.1.77", "Wi-Fi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211),
        });
        Check.That("an unplugged adapter is not offered", withDown.Count == 1);
        Check.That("and the one that is up still is", withDown[0].Address == "192.168.1.77");

        // Two adapters reporting the same address must not produce a duplicate row.
        var duplicated = LocalNetwork.Select(new[]
        {
            Up("192.168.1.77", "Wi-Fi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211),
            Up("192.168.1.77", "Wi-Fi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211),
        });
        Check.That("a repeated address is listed once", duplicated.Count == 1);

        var none = LocalNetwork.Select(Array.Empty<AddressCandidate>());
        Check.That("a machine with no network yields nothing rather than a fake address",
            none.Count == 0);
    }

    private static void Ranking()
    {
        Check.Section("38. Ordering addresses by what a phone can actually reach");

        var mixed = LocalNetwork.Select(new[]
        {
            Up("172.30.96.1", "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter",
                NetworkInterfaceType.Ethernet),
            Up("192.168.56.1", "VirtualBox Host-Only Network",
                "VirtualBox Host-Only Ethernet Adapter", NetworkInterfaceType.Ethernet),
            Up("192.168.1.77", "Wi-Fi", "Intel(R) Wi-Fi 6 AX200", NetworkInterfaceType.Wireless80211),
            Up("192.168.1.40", "Ethernet", "Realtek Gaming GbE", NetworkInterfaceType.Ethernet),
        });

        Check.That("all four are still listed, nothing hidden", mixed.Count == 4);
        Check.That("a real adapter is offered first, not a hypervisor's",
            !mixed[0].IsVirtual);
        Check.That("Wi-Fi leads when both real adapters are equal",
            mixed[0].Address == "192.168.1.77");
        Check.That("the wired LAN is second, not buried under virtual adapters",
            mixed[1].Address == "192.168.1.40");
        Check.That("virtual adapters sink to the bottom",
            mixed[2].IsVirtual && mixed[3].IsVirtual);

        // A Hyper-V switch is named nothing like "virtual" on some machines, so the
        // description is checked too.
        var byDescription = LocalNetwork.Select(new[]
        {
            Up("10.0.75.1", "Local Area Connection 2", "VMware Virtual Ethernet Adapter for VMnet8",
                NetworkInterfaceType.Ethernet),
        });
        Check.That("a virtual adapter is spotted by its description alone",
            byDescription[0].IsVirtual);

        // 169.254.x means no DHCP answered. Offer it, but last.
        var selfAssigned = LocalNetwork.Select(new[]
        {
            Up("169.254.12.9", "Ethernet 2", "Realtek USB GbE", NetworkInterfaceType.Ethernet),
            Up("192.168.1.77", "Wi-Fi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211),
        });
        Check.That("a self-assigned address is offered last", selfAssigned[1].Address == "169.254.12.9");
        Check.That("but is still offered, in case it is a direct cable", selfAssigned.Count == 2);

        // A phone tethered over a cable arrives as an ordinary Ethernet adapter whose
        // description is the only clue. It must be recognised, labelled, and offered
        // first: it is the most direct path there is and needs no Wi-Fi at all.
        var tethered = LocalNetwork.Select(new[]
        {
            Up("192.168.1.77", "Wi-Fi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211),
            Up("192.168.42.129", "Ethernet 5", "Remote NDIS based Internet Sharing Device",
                NetworkInterfaceType.Ethernet),
            Up("172.30.96.1", "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter",
                NetworkInterfaceType.Ethernet),
        });

        Check.That("a phone tethered over USB is offered first",
            tethered[0].Address == "192.168.42.129");
        Check.That("and is labelled as a cable", tethered[0].KindLabel == "USB cable");
        Check.That("it is never mistaken for a virtual adapter", !tethered[0].IsVirtual);
        Check.That("Wi-Fi still comes next", tethered[1].Address == "192.168.1.77");
        Check.That("and the hypervisor's adapter is still last", tethered[2].IsVirtual);

        Check.That("a home-range address is marked as private",
            mixed.First(a => a.Address == "192.168.1.77").IsPrivateRange);
        Check.That("Wi-Fi is labelled for the user", mixed[0].KindLabel == "Wi-Fi");
        Check.That("wired is labelled for the user",
            mixed.First(a => a.Address == "192.168.1.40").KindLabel == "Wired");
    }

    private static void LiveMachine()
    {
        Check.Section("39. Reading this machine's real adapters");

        // Whatever this machine looks like, the lookup must not throw and must not
        // invent something unreachable.
        IReadOnlyList<LocalAddress> live = LocalNetwork.Detect();
        Check.That("detection returns without throwing", true);
        Check.That("no loopback address is ever reported",
            live.All(a => !a.Address.StartsWith("127.", StringComparison.Ordinal)));
        Check.That("every reported address is a well-formed IPv4",
            live.All(a => System.Net.IPAddress.TryParse(a.Address, out _)));
        Check.That("every reported address names the adapter it belongs to",
            live.All(a => a.InterfaceName.Length > 0));
        Check.Note($"this machine reports {live.Count} usable address(es): " +
            (live.Count == 0 ? "none" : string.Join(", ", live.Select(a => $"{a.Address} ({a.KindLabel}, {a.InterfaceName})"))));
    }
}
