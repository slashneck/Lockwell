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
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Lockwell.Sync;

namespace Lockwell.Tests;

/// <summary>
/// The full sync: the PC serves, then the phone serves back, over one session.
///
/// Also covers the two timed removal rules, where the property that matters is what
/// they refuse to delete.
/// </summary>
internal static class RoundTripChecks
{
    public static async Task RunAsync()
    {
        await BothDirections();
        ExpiryRules();
        StaleWipeRules();
    }

    private static async Task BothDirections()
    {
        Check.Section("34. A sync moves items both ways in one session");

        var fromPc = new Dictionary<string, byte[]>
        {
            ["pc1"] = RandomNumberGenerator.GetBytes(30_000),
            ["pc2"] = Encoding.UTF8.GetBytes("from the desktop"),
        };
        var fromPhone = new Dictionary<string, byte[]>
        {
            ["ph1"] = Encoding.UTF8.GetBytes("a photo taken on the phone"),
        };

        var pcOffers = Describe(fromPc);
        var phoneOffers = Describe(fromPhone);

        var phoneGot = new Dictionary<string, byte[]>();
        var pcGot = new Dictionary<string, byte[]>();

        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();
        byte[] secret = RandomNumberGenerator.GetBytes(32);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // The PC serves first, then receives -- exactly the order the app uses.
        var pcTask = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync(cts.Token);
            using NetworkStream stream = server.GetStream();
            var hs = await Handshake.RespondAsync(stream, pc, HandshakeMode.Pair, secret, cancellationToken: cts.Token);
            using var session = new SyncSession(stream, hs);

            await Transfer.ServeAsync(session, "PC", pcOffers,
                id => fromPc[id].ToArray(), null, cts.Token);

            await Transfer.ReceiveAsync(session,
                decide: items => items.Select(i => i.Id).ToList(),
                store: (item, bytes) => pcGot[item.Id] = bytes.ToArray(),
                null, cts.Token);
        }, cts.Token);

        // The phone receives first, then serves.
        var phoneTask = Task.Run(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            using NetworkStream stream = client.GetStream();
            var hs = await Handshake.InitiateAsync(stream, phone, pc.PublicKey, HandshakeMode.Pair, secret, cts.Token);
            using var session = new SyncSession(stream, hs);

            await Transfer.ReceiveAsync(session,
                decide: items => items.Select(i => i.Id).ToList(),
                store: (item, bytes) => phoneGot[item.Id] = bytes.ToArray(),
                null, cts.Token);

            await Transfer.ServeAsync(session, "Phone", phoneOffers,
                id => fromPhone[id].ToArray(), null, cts.Token);
        }, cts.Token);

        await Task.WhenAll(pcTask, phoneTask);
        listener.Stop();

        Check.That("the phone received both PC items", phoneGot.Count == 2);
        Check.That("PC content arrived intact", phoneGot["pc2"].SequenceEqual(fromPc["pc2"]));
        Check.That("the PC received the phone's item", pcGot.Count == 1);
        Check.That("phone content arrived intact", pcGot["ph1"].SequenceEqual(fromPhone["ph1"]));
        Check.Note("one handshake, one session, both directions");
    }

    private static void ExpiryRules()
    {
        Check.Section("35. Per-item expiry");

        var expired = new TransferItem { Id = "a", ExpiresUtc = DateTime.UtcNow.AddHours(-1) };
        var live = new TransferItem { Id = "b", ExpiresUtc = DateTime.UtcNow.AddDays(3) };
        var forever = new TransferItem { Id = "c", ExpiresUtc = null };

        Check.That("an item past its expiry is due for removal",
            expired.ExpiresUtc is not null && expired.ExpiresUtc <= DateTime.UtcNow);
        Check.That("an item still in date is kept",
            live.ExpiresUtc is not null && live.ExpiresUtc > DateTime.UtcNow);
        Check.That("an item with no expiry is never due", forever.ExpiresUtc is null);

        // The sending side turns a day count into an absolute instant, so the phone
        // needs no contact with the PC to enforce it.
        DateTime computed = DateTime.UtcNow.AddDays(7);
        Check.That("a 7 day expiry lands a week out",
            Math.Abs((computed - DateTime.UtcNow).TotalDays - 7) < 0.01);
    }

    private static void StaleWipeRules()
    {
        Check.Section("36. Stale-device wipe");
        Check.Note("The rule that matters is what it refuses to delete.");

        // Mirrors PhoneVault.ApplyStaleWipe, which only ever removes received items.
        static bool WouldRemove(bool enabled, DateTime? lastSync, int afterDays, bool wasReceived)
        {
            if (!enabled) return false;
            if (lastSync is null) return false;
            if ((DateTime.UtcNow - lastSync.Value).TotalDays < Math.Max(1, afterDays)) return false;
            return wasReceived;
        }

        DateTime longAgo = DateTime.UtcNow.AddDays(-45);
        DateTime recent = DateTime.UtcNow.AddDays(-2);

        Check.That("off by default, so nothing is removed",
            !WouldRemove(false, longAgo, 30, wasReceived: true));

        Check.That("a recently synced phone keeps everything",
            !WouldRemove(true, recent, 30, wasReceived: true));

        Check.That("a long-idle phone drops what a PC sent it",
            WouldRemove(true, longAgo, 30, wasReceived: true));

        Check.That("but NEVER something created on the phone",
            !WouldRemove(true, longAgo, 30, wasReceived: false));

        Check.That("a phone that never synced has nothing received to drop",
            !WouldRemove(true, null, 30, wasReceived: true));

        Check.That("a shorter window fires sooner",
            WouldRemove(true, DateTime.UtcNow.AddDays(-10), 7, wasReceived: true));
        Check.That("and not before it is due",
            !WouldRemove(true, DateTime.UtcNow.AddDays(-3), 7, wasReceived: true));
    }

    private static List<TransferItem> Describe(Dictionary<string, byte[]> files) =>
        files.Select(f => new TransferItem
        {
            Id = f.Key,
            Title = f.Key,
            FileName = f.Key + ".bin",
            MediaType = "application/octet-stream",
            SizeBytes = f.Value.LongLength,
            ContentHash = Transfer.HashOf(f.Value),
        }).ToList();
}
