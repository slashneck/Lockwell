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
/// The handshake, run over real TCP sockets on loopback rather than a mock stream, so
/// the framing and partial-read handling are genuinely exercised.
///
/// The tests that matter most are the ones that must FAIL: a wrong pairing secret, an
/// unknown device, a substituted responder, and a tampered frame.
/// </summary>
internal static class HandshakeChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync()
    {
        await SessionCodeMatches();
        await Pairing();
        await WrongSecret();
        await Reconnect();
        await UnknownPeerRefused();
        await ImpersonatedResponder();
        await Transport();
        await TamperedFrame();
        await Chunking();
    }

    private static async Task Pairing()
    {
        Check.Section("17. Pairing handshake over TCP");

        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();
        byte[] secret = RandomNumberGenerator.GetBytes(32);

        var (initiator, responder) = await ExchangeAsync(
            phoneSide: s => Handshake.InitiateAsync(s, phone, pc.PublicKey, HandshakeMode.Pair, secret),
            pcSide: s => Handshake.RespondAsync(s, pc, HandshakeMode.Pair, secret));

        Check.That("pairing completes over a real socket", initiator is not null && responder is not null);

        Check.That("the phone learned the PC's real identity",
            initiator!.PeerPublicKey.SequenceEqual(pc.PublicKey));
        Check.That("the PC learned the phone's real identity",
            responder!.PeerPublicKey.SequenceEqual(phone.PublicKey));

        // The keys must mirror: what one sends on, the other receives on.
        Check.That("send/receive keys are mirrored between the two sides",
            initiator.SendKey.SequenceEqual(responder.ReceiveKey) &&
            initiator.ReceiveKey.SequenceEqual(responder.SendKey));
        Check.That("the two directions use different keys",
            !initiator.SendKey.SequenceEqual(initiator.ReceiveKey));

        Check.Note($"phone sees PC as   {initiator.PeerFingerprint}");
        Check.Note($"PC sees phone as   {responder.PeerFingerprint}");
        Check.That("fingerprints are what the user would compare on both screens",
            initiator.PeerFingerprint == pc.Fingerprint &&
            responder.PeerFingerprint == phone.Fingerprint);
    }

    private static async Task WrongSecret()
    {
        Check.Section("18. A wrong pairing secret must fail");

        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();

        // The attacker knows the PC's public key but photographed nothing.
        byte[] real = RandomNumberGenerator.GetBytes(32);
        byte[] guessed = RandomNumberGenerator.GetBytes(32);

        var (initiator, responder) = await ExchangeAsync(
            phoneSide: s => Handshake.InitiateAsync(s, phone, pc.PublicKey, HandshakeMode.Pair, guessed),
            pcSide: s => Handshake.RespondAsync(s, pc, HandshakeMode.Pair, real));

        // The handshake itself can still complete: the secret only affects the final
        // key derivation. What must not happen is the two sides agreeing on keys.
        bool keysMatch = initiator is not null && responder is not null &&
                         initiator.SendKey.SequenceEqual(responder.ReceiveKey);

        Check.That("a guessed pairing secret never reaches the same keys", !keysMatch);

        if (initiator is not null && responder is not null)
        {
            bool talked = await CanTalkAsync(initiator, responder);
            Check.That("and therefore no data can flow between them", !talked);
        }
    }

    private static async Task Reconnect()
    {
        Check.Section("19. Reconnect without a QR code");

        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();

        var (initiator, responder) = await ExchangeAsync(
            phoneSide: s => Handshake.InitiateAsync(s, phone, pc.PublicKey, HandshakeMode.Reconnect),
            pcSide: s => Handshake.RespondAsync(s, pc, HandshakeMode.Reconnect,
                isTrusted: key => key.SequenceEqual(phone.PublicKey)));

        Check.That("a paired device reconnects with no user interaction",
            initiator is not null && responder is not null);
        Check.That("keys still mirror correctly",
            initiator!.SendKey.SequenceEqual(responder!.ReceiveKey));

        // Two reconnects must never produce the same keys.
        var (again, _) = await ExchangeAsync(
            phoneSide: s => Handshake.InitiateAsync(s, phone, pc.PublicKey, HandshakeMode.Reconnect),
            pcSide: s => Handshake.RespondAsync(s, pc, HandshakeMode.Reconnect,
                isTrusted: key => key.SequenceEqual(phone.PublicKey)));

        Check.That("a second session derives entirely fresh keys (forward secrecy)",
            !again!.SendKey.SequenceEqual(initiator.SendKey));
    }

    private static async Task UnknownPeerRefused()
    {
        Check.Section("20. An unpaired device is refused");

        var pc = DeviceIdentity.Create();
        var stranger = DeviceIdentity.Create();
        var knownPhone = DeviceIdentity.Create();

        HandshakeFailure failure = HandshakeFailure.None;
        var (initiator, responder) = await ExchangeAsync(
            phoneSide: s => Handshake.InitiateAsync(s, stranger, pc.PublicKey, HandshakeMode.Reconnect),
            pcSide: async s =>
            {
                try
                {
                    return await Handshake.RespondAsync(s, pc, HandshakeMode.Reconnect,
                        isTrusted: key => key.SequenceEqual(knownPhone.PublicKey));
                }
                catch (HandshakeException ex)
                {
                    failure = ex.Reason;
                    throw;
                }
            });

        Check.That("the PC refuses a device it has never paired with", responder is null);
        Check.That("and reports it as an unknown peer", failure == HandshakeFailure.UnknownPeer);
    }

    private static async Task ImpersonatedResponder()
    {
        Check.Section("21. A substituted PC cannot answer");
        Check.Note("This is what stops someone standing up a fake Lockwell on the network.");

        var realPc = DeviceIdentity.Create();
        var attacker = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();
        byte[] secret = RandomNumberGenerator.GetBytes(32);

        // The phone scanned the real PC's QR, but an attacker answers instead.
        var (initiator, responder) = await ExchangeAsync(
            phoneSide: s => Handshake.InitiateAsync(s, phone, realPc.PublicKey, HandshakeMode.Pair, secret),
            pcSide: s => Handshake.RespondAsync(s, attacker, HandshakeMode.Pair, secret));

        bool keysMatch = initiator is not null && responder is not null &&
                         initiator.SendKey.SequenceEqual(responder.ReceiveKey);

        Check.That("an impostor cannot reach the same keys as the phone", !keysMatch);

        // If the handshake refused outright that counts too, so assert explicitly
        // rather than skipping the check when one side is null.
        bool impostorCanRead = initiator is not null && responder is not null &&
                               await CanTalkAsync(initiator, responder);
        Check.That("and therefore cannot read anything the phone sends", !impostorCanRead);
    }

    private static async Task Transport()
    {
        Check.Section("22. Encrypted transport");

        var (a, b, streamA, streamB) = await PairedSessionsAsync();
        using (a) using (b) using (streamA) using (streamB)
        {
            byte[] message = Encoding.UTF8.GetBytes("a secret that must not appear on the wire");
            await a.SendAsync(message);
            byte[] received = await b.ReceiveAsync();

            Check.That("a message survives the tunnel intact", received.SequenceEqual(message));

            // Many frames in sequence, to exercise the counter.
            for (int i = 0; i < 50; i++)
                await a.SendAsync(Encoding.UTF8.GetBytes($"frame-{i}"));

            bool allGood = true;
            for (int i = 0; i < 50; i++)
            {
                byte[] frame = await b.ReceiveAsync();
                if (Encoding.UTF8.GetString(frame) != $"frame-{i}") allGood = false;
            }
            Check.That("50 sequential frames all arrive in order and intact", allGood);

            // Both directions.
            await b.SendAsync(Encoding.UTF8.GetBytes("reply"));
            Check.That("the tunnel works in both directions",
                Encoding.UTF8.GetString(await a.ReceiveAsync()) == "reply");
        }
    }

    private static async Task TamperedFrame()
    {
        Check.Section("23. Tampering is caught");

        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();
        byte[] secret = RandomNumberGenerator.GetBytes(32);

        // Capture what actually goes over the wire, then corrupt one byte of it.
        var captured = new MemoryStream();
        var (initiator, _) = await ExchangeAsync(
            phoneSide: s => Handshake.InitiateAsync(s, phone, pc.PublicKey, HandshakeMode.Pair, secret),
            pcSide: s => Handshake.RespondAsync(s, pc, HandshakeMode.Pair, secret));

        var toWire = new MemoryStream();
        using (var sender = new SyncSession(toWire, initiator!))
            await sender.SendAsync(Encoding.UTF8.GetBytes("the original message"));

        byte[] wire = toWire.ToArray();
        Check.That("the plaintext is not visible on the wire",
            !Encoding.UTF8.GetString(wire).Contains("original"));

        // Flip a bit in the ciphertext.
        wire[^1] ^= 0x01;

        var receiver = new SyncSession(new MemoryStream(wire), MirrorOf(initiator!));
        bool rejected = false;
        try { await receiver.ReceiveAsync(); }
        catch (InvalidDataException) { rejected = true; }

        Check.That("a single flipped bit makes the frame fail to authenticate", rejected);
    }

    private static async Task Chunking()
    {
        Check.Section("24. Large payload chunking");

        byte[] big = RandomNumberGenerator.GetBytes(SyncProtocol.MaxFramePayload * 2 + 1234);
        var chunks = SyncSession.Chunk(big).ToList();

        Check.That("a large payload splits into multiple frames", chunks.Count == 3);
        Check.That("no chunk exceeds the frame limit",
            chunks.All(c => c.Length <= SyncProtocol.MaxFramePayload));
        Check.That("chunks reassemble to exactly the original",
            chunks.SelectMany(c => c.ToArray()).ToArray().SequenceEqual(big));

        var (a, b, sa, sb) = await PairedSessionsAsync();
        using (a) using (b) using (sa) using (sb)
        {
            byte[] payload = RandomNumberGenerator.GetBytes(SyncProtocol.MaxFramePayload + 500);
            var parts = SyncSession.Chunk(payload).ToList();

            // Send and receive concurrently. Queueing several megabyte-sized frames
            // before reading any of them fills the socket's send buffer and deadlocks
            // both ends -- which is exactly what a real file transfer would hit.
            var rebuilt = new List<byte>();
            var receiving = Task.Run(async () =>
            {
                for (int i = 0; i < parts.Count; i++)
                    rebuilt.AddRange(await b.ReceiveAsync());
            });

            foreach (var part in parts) await a.SendAsync(part.ToArray());
            await receiving.WaitAsync(TimeSpan.FromSeconds(20));

            Check.That("a multi-frame file arrives byte-identical",
                rebuilt.ToArray().SequenceEqual(payload));
        }
    }

    // ------------------------------------------------------------- helpers

    /// <summary>Swap send and receive, to build the other end of a session locally.</summary>
    private static HandshakeResult MirrorOf(HandshakeResult result) => new()
    {
        PeerPublicKey = result.PeerPublicKey,
        SendKey = result.ReceiveKey,
        ReceiveKey = result.SendKey,

        // The real thing derives this from the shared transcript, so the two ends always
        // agree; a locally mirrored copy just carries the same value across.
        SessionCode = result.SessionCode,
    };

    /// <summary>True if the two sides can actually exchange a message.</summary>
    private static async Task<bool> CanTalkAsync(HandshakeResult a, HandshakeResult b)
    {
        try
        {
            var wire = new MemoryStream();
            using (var sender = new SyncSession(wire, a))
                await sender.SendAsync(Encoding.UTF8.GetBytes("probe"));

            using var receiver = new SyncSession(new MemoryStream(wire.ToArray()), b);
            byte[] got = await receiver.ReceiveAsync();
            return Encoding.UTF8.GetString(got) == "probe";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Run both halves of a handshake against each other over loopback TCP.</summary>
    private static async Task<(HandshakeResult? Initiator, HandshakeResult? Responder)> ExchangeAsync(
        Func<Stream, Task<HandshakeResult>> phoneSide,
        Func<Stream, Task<HandshakeResult>> pcSide)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(Timeout);

        var acceptTask = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync(cts.Token);
            using NetworkStream stream = server.GetStream();
            return await pcSide(stream);
        }, cts.Token);

        var connectTask = Task.Run(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            using NetworkStream stream = client.GetStream();
            return await phoneSide(stream);
        }, cts.Token);

        HandshakeResult? initiator = null;
        HandshakeResult? responder = null;

        try { responder = await acceptTask; } catch { /* expected in refusal tests */ }
        try { initiator = await connectTask; } catch { /* expected in refusal tests */ }

        listener.Stop();
        return (initiator, responder);
    }

    /// <summary>Two live sessions wired together over loopback.</summary>
    private static async Task<(SyncSession A, SyncSession B, Stream StreamA, Stream StreamB)>
        PairedSessionsAsync()
    {
        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();
        byte[] secret = RandomNumberGenerator.GetBytes(32);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        TcpClient? server = null;
        var client = new TcpClient();

        var acceptTask = Task.Run(async () => server = await listener.AcceptTcpClientAsync());
        await client.ConnectAsync(IPAddress.Loopback, port);
        await acceptTask;

        Stream clientStream = client.GetStream();
        Stream serverStream = server!.GetStream();

        var responderTask = Handshake.RespondAsync(serverStream, pc, HandshakeMode.Pair, secret);
        var initiatorTask = Handshake.InitiateAsync(clientStream, phone, pc.PublicKey, HandshakeMode.Pair, secret);

        HandshakeResult responder = await responderTask;
        HandshakeResult initiator = await initiatorTask;

        listener.Stop();
        return (new SyncSession(clientStream, initiator),
                new SyncSession(serverStream, responder),
                clientStream,
                serverStream);
    }

    /// <summary>
    /// The number both screens show, and the reason it is not a device fingerprint.
    ///
    /// This exists because of a bug a user found by simply reading both screens: each side
    /// displayed the *other* device's identity fingerprint, so the two never matched, and
    /// pairing succeeded anyway. The step looked like a security check and verified nothing.
    ///
    /// The code now comes from the finished handshake transcript, which both sides compute
    /// identically only if they actually talked to each other. That is what makes comparing
    /// it meaningful.
    /// </summary>
    private static async Task SessionCodeMatches()
    {
        Check.Section("53. The confirmation code both devices show");

        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();

        string code = PairingCode.Generate();
        byte[] secret = PairingCode.ToSecret(code, pc.PublicKey);

        var (initiator, responder) = await ExchangeAsync(
            phoneSide: st => Handshake.InitiateAsync(st, phone, pc.PublicKey, HandshakeMode.Pair, secret),
            pcSide: st => Handshake.RespondAsync(st, pc, HandshakeMode.Pair, secret));

        if (initiator is null || responder is null)
        {
            Check.That("pairing completed so the code can be compared", false);
            return;
        }

        Check.That("both devices derive a confirmation code",
            initiator.SessionCode.Length > 0 && responder.SessionCode.Length > 0);

        Check.That($"and it is the same on both ({initiator.SessionCode})",
            initiator.SessionCode == responder.SessionCode);

        Check.That("it is six digits, grouped for reading",
            System.Text.RegularExpressions.Regex.IsMatch(initiator.SessionCode, @"^\d{3} \d{3}$"));

        // The old behaviour, kept as a check so it cannot come back: the two identity
        // fingerprints are different by nature, which is exactly why showing them to be
        // compared was meaningless.
        Check.That("the two identity fingerprints differ, as they always would",
            initiator.PeerFingerprint != responder.PeerFingerprint);
        Check.That("so the confirmation code is not either device's fingerprint",
            initiator.SessionCode != initiator.PeerFingerprint &&
            initiator.SessionCode != responder.PeerFingerprint);

        // A second, unrelated pairing must not land on the same number.
        var otherPhone = DeviceIdentity.Create();
        string code2 = PairingCode.Generate();
        byte[] secret2 = PairingCode.ToSecret(code2, pc.PublicKey);

        var (a2, b2) = await ExchangeAsync(
            phoneSide: st => Handshake.InitiateAsync(st, otherPhone, pc.PublicKey, HandshakeMode.Pair, secret2),
            pcSide: st => Handshake.RespondAsync(st, pc, HandshakeMode.Pair, secret2));

        Check.That("a different pairing produces a different code",
            a2!.SessionCode != initiator.SessionCode);
        Check.That("and that pair still agrees with itself", a2.SessionCode == b2!.SessionCode);

        // Same devices, run again: fresh ephemerals mean a fresh code, so a number seen
        // once is not a number that can be replayed later.
        var (a3, b3) = await ExchangeAsync(
            phoneSide: st => Handshake.InitiateAsync(st, phone, pc.PublicKey, HandshakeMode.Pair, secret),
            pcSide: st => Handshake.RespondAsync(st, pc, HandshakeMode.Pair, secret));

        Check.That("the same two devices get a new code each time",
            a3!.SessionCode != initiator.SessionCode);
        Check.That("still agreeing with each other", a3.SessionCode == b3!.SessionCode);
    }

}
