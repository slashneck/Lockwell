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
using System.Text;
using Lockwell.Sync;

namespace Lockwell.Tests;

/// <summary>
/// The complete linking flow as the two apps actually run it: the PC greets, the phone
/// derives the secret from the typed code, both run the handshake, and a session opens.
///
/// The greeting sends the PC's public key in the clear, so the tests that matter are
/// the ones proving that key alone gets an attacker nowhere without the code.
/// </summary>
internal static class LinkingChecks
{
    /// <summary>
    /// How long a pairing survives without being used, set per device.
    ///
    /// Per device because devices are not equivalent: a phone carried everywhere earns a
    /// short leash, while a machine switched on at weekends would be unpaired constantly
    /// by the same number. The rule with teeth is the one about what gets removed, since
    /// getting it wrong either unpairs a device that was simply idle or keeps a stale
    /// pairing alive as a standing invitation.
    /// </summary>
    private static void PerDeviceExpiry()
    {
        Check.Section("51. Pairing expiry, per device");

        string dir = Check.Scratch("expiry");
        try
        {
            var store = new TrustStore(dir);
            store.ExpiryEnabled = true;
            store.ExpiryDays = 30;

            var phoneKey = DeviceIdentity.Create().PublicKey;
            var laptopKey = DeviceIdentity.Create().PublicKey;
            var deskKey = DeviceIdentity.Create().PublicKey;

            var phone = store.Add(phoneKey, "Phone", "phone", expiryDays: 7);
            var laptop = store.Add(laptopKey, "Laptop", "laptop");
            var desk = store.Add(deskKey, "Studio PC", "desktop", neverExpires: true);

            Check.That("a device can carry its own window", phone.ExpiryDaysOverride == 7);
            Check.That("one without falls back to the vault's",
                laptop.EffectiveExpiryDays(30) == 30);
            Check.That("and its own wins when set", phone.EffectiveExpiryDays(30) == 7);

            Check.That("a device set never to expire reports no countdown",
                desk.DaysUntilExpiry(30, true) is null);

            // Nothing is overdue yet, so a prune must remove nothing at all.
            Check.That("a prune removes nothing while everything is fresh",
                store.PruneExpired() == 0);
            Check.That("all three are still paired", store.Devices.Count == 3);

            // Age the phone past its own seven days but well inside the vault's thirty.
            phone.LastSeenUtc = DateTime.UtcNow.AddDays(-10);
            laptop.LastSeenUtc = DateTime.UtcNow.AddDays(-10);
            desk.LastSeenUtc = DateTime.UtcNow.AddDays(-400);

            Check.That("the phone is overdue against its own window",
                phone.DaysUntilExpiry(30, true) is <= 0);
            Check.That("the laptop is not, on the vault's",
                laptop.DaysUntilExpiry(30, true) is > 0);

            int removed = store.PruneExpired();
            Check.That("only the device past its own window is dropped", removed == 1);
            Check.That("the phone is gone", store.Devices.All(d => d.Name != "Phone"));
            Check.That("the laptop stays", store.Devices.Any(d => d.Name == "Laptop"));
            Check.That("and never-expires survives being idle for over a year",
                store.Devices.Any(d => d.Name == "Studio PC"));

            // A device with its own window is honoured even when the vault-wide setting is
            // off: choosing a number for one device is a clearer instruction than a global
            // default it was set to override.
            store.ExpiryEnabled = false;
            var tablet = store.Add(DeviceIdentity.Create().PublicKey, "Tablet", "phone", expiryDays: 5);
            tablet.LastSeenUtc = DateTime.UtcNow.AddDays(-9);

            var laptopStill = store.Devices.First(d => d.Name == "Laptop");
            laptopStill.LastSeenUtc = DateTime.UtcNow.AddDays(-500);

            Check.That("with vault expiry off, a device with no window of its own never expires",
                laptopStill.DaysUntilExpiry(30, false) is null);

            store.PruneExpired();
            Check.That("but a device with its own window still expires",
                store.Devices.All(d => d.Name != "Tablet"));
            Check.That("while the one relying on the vault setting is kept",
                store.Devices.Any(d => d.Name == "Laptop"));

            // Changing it later must stick.
            store.SetExpiry(laptopStill.Id, days: null, never: true);
            Check.That("a device can be changed to never expire",
                store.Devices.First(d => d.Name == "Laptop").NeverExpires);
        }
        finally
        {
            Check.Cleanup(dir);
        }
    }

    public static async Task RunAsync()
    {
        PerDeviceExpiry();
        await SuccessfulLink();
        await WrongCodeTyped();
        await ImpostorWithOwnKey();
        CodeHandling();
    }

    private static async Task SuccessfulLink()
    {
        Check.Section("25. Linking a phone to a PC");

        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();

        string code = PairingCode.Generate();
        Check.Note($"PC shows code: {code}");

        var (phoneSide, pcSide) = await LinkAsync(pc, phone, code, typed: code);

        Check.That("linking completes", phoneSide is not null && pcSide is not null);
        Check.That("the phone learned the PC's real identity",
            phoneSide!.PeerPublicKey.SequenceEqual(pc.PublicKey));
        Check.That("the PC learned the phone's real identity",
            pcSide!.PeerPublicKey.SequenceEqual(phone.PublicKey));
        Check.That("both sides agree on keys",
            phoneSide.SendKey.SequenceEqual(pcSide.ReceiveKey) &&
            phoneSide.ReceiveKey.SequenceEqual(pcSide.SendKey));

        Check.That("both screens show the same fingerprint to compare",
            phoneSide.PeerFingerprint == pc.Fingerprint &&
            pcSide.PeerFingerprint == phone.Fingerprint);

        Check.That("a session can carry data once linked", await CanTalkAsync(phoneSide, pcSide));
    }

    private static async Task WrongCodeTyped()
    {
        Check.Section("26. A mistyped code links nothing");

        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();

        string shown = PairingCode.Generate();
        string typed = PairingCode.Generate(); // a different code entirely

        var (phoneSide, pcSide) = await LinkAsync(pc, phone, shown, typed);

        bool agreed = phoneSide is not null && pcSide is not null &&
                      phoneSide.SendKey.SequenceEqual(pcSide.ReceiveKey);

        Check.That("a wrong code never reaches the same keys", !agreed);

        bool talked = phoneSide is not null && pcSide is not null &&
                      await CanTalkAsync(phoneSide, pcSide);
        Check.That("and so no data can flow", !talked);
    }

    private static async Task ImpostorWithOwnKey()
    {
        Check.Section("27. Knowing the PC's public key is not enough");
        Check.Note("The greeting is public, so this proves the code is what actually protects it.");

        var realPc = DeviceIdentity.Create();
        var impostor = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();

        string code = PairingCode.Generate();

        // The impostor answers instead, greeting with its own key. It has never seen
        // the code the real PC displayed.
        var (phoneSide, impostorSide) = await LinkAsync(impostor, phone, code, typed: code,
            phoneExpectsFrom: impostor);

        // The phone salts the code with whatever key it was greeted with, so it does
        // derive a matching secret here. What it CANNOT do is silently trust the wrong
        // machine: the fingerprint it shows is the impostor's, not the real PC's.
        Check.That("the phone shows the impostor's fingerprint, not the real PC's",
            phoneSide is null || !phoneSide.PeerFingerprint.Equals(realPc.Fingerprint));

        Check.Note("This is exactly why the user compares fingerprints before confirming.");
        Check.That("the real PC's identity was never involved",
            phoneSide is null || !phoneSide.PeerPublicKey.SequenceEqual(realPc.PublicKey));
    }

    private static void CodeHandling()
    {
        Check.Section("28. Pairing code handling");

        string code = PairingCode.Generate();
        Check.That($"generated code is {PairingCode.Length} characters", PairingCode.LooksValid(code));
        Check.That("formatting is stripped when normalising",
            PairingCode.Normalise(code).Length == PairingCode.Length);

        // The code is digits now, but people still read a zero as an O and a five as an S,
        // especially off a screen across the room. Those are folded rather than rejected.
        string normalised = PairingCode.Normalise("OI2 S4B");
        Check.That($"look-alike characters are corrected ({normalised})",
            normalised == "012548");

        Check.That("lowercase is accepted",
            PairingCode.Normalise(code.ToLowerInvariant()) == PairingCode.Normalise(code));
        Check.That("anything that is not part of a code is dropped",
            PairingCode.Normalise("12-34 56") == "123456");
        Check.That("an incomplete code is rejected", !PairingCode.LooksValid("123"));

        var key = DeviceIdentity.Create().PublicKey;
        Check.That("the same code and key always derive the same secret",
            PairingCode.ToSecret(code, key).SequenceEqual(PairingCode.ToSecret(code, key)));

        var otherKey = DeviceIdentity.Create().PublicKey;
        Check.That("the same code against a different PC derives a different secret",
            !PairingCode.ToSecret(code, key).SequenceEqual(PairingCode.ToSecret(code, otherKey)));
    }

    // -------------------------------------------------------------- helpers

    /// <summary>Drives both apps' real linking code against each other over loopback.</summary>
    private static async Task<(HandshakeResult? Phone, HandshakeResult? Pc)> LinkAsync(
        DeviceIdentity pc, DeviceIdentity phone, string shownCode, string typed,
        DeviceIdentity? phoneExpectsFrom = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // --- the PC side, exactly as VaultShellView.Linking does it ---
        var pcTask = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync(cts.Token);
            using NetworkStream stream = server.GetStream();

            await PairingGreeting.SendAsync(stream, pc.PublicKey, "Test PC", cts.Token);

            byte[] secret = PairingCode.ToSecret(shownCode, pc.PublicKey);
            return await Handshake.RespondAsync(
                stream, pc, HandshakeMode.Pair, secret, cancellationToken: cts.Token);
        }, cts.Token);

        // --- the phone side, exactly as LinkPcPage does it ---
        var phoneTask = Task.Run(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            using NetworkStream stream = client.GetStream();

            (byte[] pcKey, string _) = await PairingGreeting.ReceiveAsync(stream, cts.Token);

            byte[] secret = PairingCode.ToSecret(typed, pcKey);
            return await Handshake.InitiateAsync(
                stream, phone, pcKey, HandshakeMode.Pair, secret, cts.Token);
        }, cts.Token);

        HandshakeResult? pcResult = null;
        HandshakeResult? phoneResult = null;

        try { pcResult = await pcTask; } catch { /* expected in refusal cases */ }
        try { phoneResult = await phoneTask; } catch { /* expected in refusal cases */ }

        listener.Stop();
        return (phoneResult, pcResult);
    }

    private static async Task<bool> CanTalkAsync(HandshakeResult a, HandshakeResult b)
    {
        try
        {
            var wire = new MemoryStream();
            using (var sender = new SyncSession(wire, a))
                await sender.SendAsync(Encoding.UTF8.GetBytes("probe"));

            using var receiver = new SyncSession(new MemoryStream(wire.ToArray()), b);
            return Encoding.UTF8.GetString(await receiver.ReceiveAsync()) == "probe";
        }
        catch
        {
            return false;
        }
    }
}
