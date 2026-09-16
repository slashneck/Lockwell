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
/// Transferring items over a live session, run the way the two apps run it.
///
/// The properties worth proving are the restrictive ones: a receiver cannot be pushed
/// something it did not ask for, an unlinked device gets nothing, and identical items
/// are not re-sent.
/// </summary>
internal static class TransferChecks
{
    public static async Task RunAsync()
    {
        await ExpiryTravelsWithTheItem();
        await FullTransfer();
        await SkipsWhatIsAlreadyThere();
        await ReceiverControlsWhatArrives();
        await UnlinkedDeviceGetsNothing();
        await CorruptedContentsRejected();
    }

    private static async Task FullTransfer()
    {
        Check.Section("29. Transferring items");

        var files = new Dictionary<string, byte[]>
        {
            ["a"] = RandomNumberGenerator.GetBytes(40_000),
            ["b"] = Encoding.UTF8.GetBytes("a small note"),
            ["c"] = RandomNumberGenerator.GetBytes(SyncProtocol.MaxFramePayload + 5_000),
        };

        var offered = files.Select(f => new TransferItem
        {
            Id = f.Key,
            Title = "Item " + f.Key,
            FileName = f.Key + ".bin",
            MediaType = "application/octet-stream",
            SizeBytes = f.Value.LongLength,
            ContentHash = Transfer.HashOf(f.Value),
        }).ToList();

        var received = new Dictionary<string, byte[]>();

        var summary = await RunTransferAsync(
            offered,
            // A copy, because the sender zeroes the plaintext once it is framed.
            load: id => files[id].ToArray(),
            decide: items => items.Select(i => i.Id).ToList(),
            store: (item, bytes) => received[item.Id] = bytes.ToArray());

        Check.That("all three items arrive", received.Count == 3);
        Check.That("small file is byte-identical", received["b"].SequenceEqual(files["b"]));
        Check.That("large multi-frame file is byte-identical", received["c"].SequenceEqual(files["c"]));
        Check.That("the summary counts what arrived", summary?.Received == 3);
        Check.Note($"moved {summary?.Bytes:N0} bytes across the encrypted session");
    }

    private static async Task SkipsWhatIsAlreadyThere()
    {
        Check.Section("30. Nothing already held is re-sent");

        byte[] shared = RandomNumberGenerator.GetBytes(20_000);
        byte[] fresh = RandomNumberGenerator.GetBytes(9_000);

        var offered = new List<TransferItem>
        {
            new() { Id = "old", Title = "Already here", FileName = "old.bin",
                    SizeBytes = shared.LongLength, ContentHash = Transfer.HashOf(shared) },
            new() { Id = "new", Title = "New", FileName = "new.bin",
                    SizeBytes = fresh.LongLength, ContentHash = Transfer.HashOf(fresh) },
        };

        var files = new Dictionary<string, byte[]> { ["old"] = shared, ["new"] = fresh };
        var alreadyHave = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Transfer.HashOf(shared),
        };

        int loads = 0;
        var received = new List<string>();

        var summary = await RunTransferAsync(
            offered,
            load: id => { loads++; return files[id].ToArray(); },
            // The receiver's real rule: take anything whose contents it does not hold.
            decide: items => items.Where(i => !alreadyHave.Contains(i.ContentHash))
                                  .Select(i => i.Id).ToList(),
            store: (item, _) => received.Add(item.Id));

        Check.That("only the new item arrives", received.Count == 1 && received[0] == "new");
        Check.That("the duplicate was reported as skipped", summary?.Skipped == 1);
        Check.That("the sender never even decrypted the duplicate", loads == 1);
    }

    private static async Task ReceiverControlsWhatArrives()
    {
        Check.Section("31. A sender cannot push what was not asked for");
        Check.Note("This is what keeps \"the phone only holds what you chose\" true.");

        var files = new Dictionary<string, byte[]>
        {
            ["wanted"] = Encoding.UTF8.GetBytes("asked for"),
            ["sneaky"] = Encoding.UTF8.GetBytes("never requested"),
        };

        var offered = files.Select(f => new TransferItem
        {
            Id = f.Key,
            Title = f.Key,
            FileName = f.Key + ".txt",
            SizeBytes = f.Value.LongLength,
            ContentHash = Transfer.HashOf(f.Value),
        }).ToList();

        var received = new List<string>();

        await RunTransferAsync(
            offered,
            load: id => files[id],
            decide: _ => new[] { "wanted" },       // ask for exactly one
            store: (item, _) => received.Add(item.Id));

        Check.That("only the requested item arrives", received.Count == 1 && received[0] == "wanted");
        Check.That("the unrequested item never transferred", !received.Contains("sneaky"));
    }

    private static async Task UnlinkedDeviceGetsNothing()
    {
        Check.Section("32. An unlinked device cannot collect a transfer");

        var pc = DeviceIdentity.Create();
        var linkedPhone = DeviceIdentity.Create();
        var stranger = DeviceIdentity.Create();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        HandshakeFailure failure = HandshakeFailure.None;

        var serverTask = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync(cts.Token);
            using NetworkStream stream = server.GetStream();
            try
            {
                // Only the linked phone is allowed to collect.
                return await Handshake.RespondAsync(
                    stream, pc, HandshakeMode.Reconnect,
                    isTrusted: key => key.SequenceEqual(linkedPhone.PublicKey),
                    cancellationToken: cts.Token);
            }
            catch (HandshakeException ex)
            {
                failure = ex.Reason;
                throw;
            }
        }, cts.Token);

        var clientTask = Task.Run(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            using NetworkStream stream = client.GetStream();
            return await Handshake.InitiateAsync(
                stream, stranger, pc.PublicKey, HandshakeMode.Reconnect, cancellationToken: cts.Token);
        }, cts.Token);

        HandshakeResult? served = null;
        try { served = await serverTask; } catch { /* expected */ }
        try { await clientTask; } catch { /* expected */ }
        listener.Stop();

        Check.That("the PC refuses to serve an unlinked device", served is null);
        Check.That("and reports it as an unknown peer", failure == HandshakeFailure.UnknownPeer);
    }

    private static async Task CorruptedContentsRejected()
    {
        Check.Section("33. Contents that do not match the offer are rejected");

        byte[] real = Encoding.UTF8.GetBytes("the genuine contents");
        byte[] swapped = Encoding.UTF8.GetBytes("something else entirely");

        var offered = new List<TransferItem>
        {
            new() { Id = "x", Title = "Item", FileName = "x.txt",
                    SizeBytes = swapped.LongLength,
                    // Hash of the real file, but the sender hands over different bytes.
                    ContentHash = Transfer.HashOf(real) },
        };

        bool stored = false;
        bool rejected = false;

        try
        {
            await RunTransferAsync(
                offered,
                load: _ => swapped.ToArray(),
                decide: items => items.Select(i => i.Id).ToList(),
                store: (_, _) => stored = true);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Check.That("mismatched contents are refused", rejected);
        Check.That("and nothing was written to the vault", !stored);
    }

    // -------------------------------------------------------------- helper

    /// <summary>Runs a real serve/receive pair over loopback and returns the summary.</summary>
    private static async Task<TransferSummary?> RunTransferAsync(
        IReadOnlyList<TransferItem> offered,
        Func<string, byte[]> load,
        Func<IReadOnlyList<TransferItem>, IReadOnlyList<string>> decide,
        Action<TransferItem, byte[]> store)
    {
        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();
        byte[] secret = RandomNumberGenerator.GetBytes(32);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var serveTask = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync(cts.Token);
            using NetworkStream stream = server.GetStream();

            var handshake = await Handshake.RespondAsync(
                stream, pc, HandshakeMode.Pair, secret, cancellationToken: cts.Token);

            using var session = new SyncSession(stream, handshake);
            await Transfer.ServeAsync(session, "Test PC", offered, load, null, cts.Token);
        }, cts.Token);

        var receiveTask = Task.Run(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            using NetworkStream stream = client.GetStream();

            var handshake = await Handshake.InitiateAsync(
                stream, phone, pc.PublicKey, HandshakeMode.Pair, secret, cts.Token);

            using var session = new SyncSession(stream, handshake);
            return await Transfer.ReceiveAsync(session, decide, store, null, cts.Token);
        }, cts.Token);

        TransferSummary? summary = null;
        Exception? receiveFailure = null;

        try { summary = await receiveTask; }
        catch (Exception ex) { receiveFailure = ex; }

        try { await serveTask; } catch { /* the sender may see the receiver hang up */ }

        listener.Stop();

        if (receiveFailure is not null) throw receiveFailure;
        return summary;
    }

    /// <summary>
    /// An expiry chosen when sending actually reaches the other device.
    ///
    /// This is here because it did not. The sending side accepted the number of days as a
    /// parameter, built the manifest entry, and never wrote it to the item -- so choosing
    /// "1 day" on the PC produced an item the phone kept forever. Nothing failed, nothing
    /// was logged, and the only symptom was a file that would not go away.
    ///
    /// The receiving half was correct throughout, which is what made it invisible: every
    /// piece worked except the one line that carried the value between them.
    /// </summary>
    private static async Task ExpiryTravelsWithTheItem()
    {
        Check.Section("55. An expiry set when sending reaches the other device");

        var offered = new TransferItem
        {
            Id = "item-1",
            Title = "Holiday photo",
            FileName = "beach.jpg",
            MediaType = "image/jpeg",
            SizeBytes = 4,
            ContentHash = Transfer.HashOf(new byte[] { 1, 2, 3, 4 }),
            ExpiresUtc = DateTime.UtcNow.AddDays(1),
        };

        TransferItem? received = null;

        await RunTransferAsync(
            offered: new[] { offered },
            load: _ => new byte[] { 1, 2, 3, 4 },
            decide: items => items.Select(i => i.Id).ToList(),
            store: (item, _) => received = item);

        Check.That("the item arrives", received is not null);

        if (received is null) return;

        Check.That("its expiry arrives with it", received.ExpiresUtc is not null);

        if (received.ExpiresUtc is { } when_)
        {
            double hours = (when_ - DateTime.UtcNow).TotalHours;
            Check.That($"and is about a day out ({hours:0.0}h)", hours is > 20 and < 28);
        }

        // Sending with no expiry must stay no expiry, not become "now".
        var forever = new TransferItem
        {
            Id = "item-2",
            Title = "Keep this",
            FileName = "notes.txt",
            MediaType = "text/plain",
            SizeBytes = 4,
            ContentHash = Transfer.HashOf(new byte[] { 5, 6, 7, 8 }),
            ExpiresUtc = null,
        };

        TransferItem? kept = null;
        await RunTransferAsync(
            offered: new[] { forever },
            load: _ => new byte[] { 5, 6, 7, 8 },
            decide: items => items.Select(i => i.Id).ToList(),
            store: (item, _) => kept = item);

        Check.That("an item sent without an expiry has none", kept?.ExpiresUtc is null);
    }

}
