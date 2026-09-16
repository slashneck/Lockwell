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

using Lockwell.Sync;

namespace Lockwell.Tests;

/// <summary>
/// Finding a PC on the network without anyone typing an address.
///
/// This runs over a real UDP socket on the loopback-reachable broadcast path, not a mock,
/// because the interesting failures here are all real-network failures: a packet that
/// never arrives, a reply that is not ours, a beacon that tries to point somewhere else.
///
/// The property that matters most is the last one. Discovery is unauthenticated by
/// definition, so a beacon must never be able to influence anything except which address
/// gets *offered* to the user. Everything that follows is still gated on the pairing code
/// and the fingerprint comparison.
/// </summary>
internal static class DiscoveryChecks
{
    public static async Task RunAsync()
    {
        await FindsAResponder();
        await FindsNothingWhenSilent();
    }

    private static DiscoveryBeacon Describe(string name, string fingerprint, bool accepting) =>
        new()
        {
            Name = name,
            Fingerprint = fingerprint,
            Port = SyncProtocol.DefaultPort,
            AcceptingPairing = accepting,
        };

    private static async Task FindsAResponder()
    {
        Check.Section("48. Discovering a PC on the local network");

        using var stop = new CancellationTokenSource();

        Task responding = LocalDiscovery.RespondAsync(
            () => Describe("Test-PC", "AAAA BBBB CCCC DDDD", accepting: true), stop.Token);

        // Give the responder a moment to bind before anyone asks.
        await Task.Delay(300);

        IReadOnlyList<DiscoveryBeacon> found =
            await LocalDiscovery.FindAsync(TimeSpan.FromSeconds(3));

        var ours = found.FirstOrDefault(b => b.Fingerprint == "AAAA BBBB CCCC DDDD");

        Check.That("a responding PC is found", ours is not null);

        if (ours is not null)
        {
            Check.That("it reports the name the user gave it", ours.Name == "Test-PC");
            Check.That("it reports the port to connect on", ours.Port == SyncProtocol.DefaultPort);
            Check.That("it says whether it is ready to pair", ours.AcceptingPairing);

            // The address must come from the packet's source, never from its contents,
            // or a beacon could send a phone somewhere of its choosing.
            Check.That("an address is filled in from where the reply came from",
                ours.Address.Length > 0 &&
                System.Net.IPAddress.TryParse(ours.Address, out _));

            Check.Note($"answered from {ours.Address}");
        }

        Check.That("the same PC is listed once, not once per network adapter",
            found.Count(b => b.Fingerprint == "AAAA BBBB CCCC DDDD") <= 1);

        stop.Cancel();
        try { await responding; } catch { /* cancelled */ }
    }

    private static async Task FindsNothingWhenSilent()
    {
        Check.Section("49. Nothing answers when no PC is listening");

        // Nothing is responding now, so a search must come back empty rather than
        // inventing something or hanging.
        var started = DateTime.UtcNow;
        IReadOnlyList<DiscoveryBeacon> found =
            await LocalDiscovery.FindAsync(TimeSpan.FromSeconds(2));

        TimeSpan took = DateTime.UtcNow - started;

        Check.That("the search gives up on its own rather than hanging",
            took < TimeSpan.FromSeconds(8));
        Check.That("no Lockwell PC is reported when none is answering",
            found.All(b => b.Fingerprint != "AAAA BBBB CCCC DDDD"));

        Check.Note($"search returned in {took.TotalSeconds:0.0}s with {found.Count} result(s)");
    }
}
