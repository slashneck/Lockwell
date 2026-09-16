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

using System.Security.Cryptography;
using System.Text;
using Lockwell.Sync;

namespace Lockwell.Tests;

/// <summary>
/// The sync foundation: device identity, the pairing QR, and key derivation.
/// The property that matters most here is that the QR leaks nothing about the vault,
/// and that two devices independently derive the same transport keys while any
/// tampering makes them derive different ones.
/// </summary>
internal static class SyncChecks
{
    public static void Run()
    {
        Identity();
        Offer();
        KeyDerivation();
        ShortCode();
    }

    /// <summary>
    /// The six-digit pairing code, and the guess limiting it depends on.
    ///
    /// Worth being blunt about why this is tested rather than trusted. The code was
    /// shortened from ten characters to six digits because it is the last thing anyone has
    /// to type. Six digits is only safe because a wrong answer costs an attacker one of a
    /// handful of tries before the code is thrown away. That limit used to be an accident
    /// of how the listener was written -- it accepted one connection and closed -- and it
    /// was silently lost when the listener was changed to keep waiting. It now lives in
    /// PairingSession, and these checks are what stop it going missing again.
    /// </summary>
    private static void ShortCode()
    {
        Check.Section("50. The pairing code, and limiting guesses at it");

        string code = PairingCode.Generate();
        string digits = PairingCode.Normalise(code);

        Check.That("a code is six digits once formatting is stripped", digits.Length == 6);
        Check.That("and is only digits", digits.All(char.IsDigit));
        Check.That("it is grouped for reading", code.Contains(' '));
        Check.That("a formatted code is accepted as typed", PairingCode.LooksValid(code));
        Check.That("so is one typed without the space",
            PairingCode.LooksValid(digits));

        Check.That("letters that look like digits are folded in",
            PairingCode.Normalise("O12 34S") == "012345");
        Check.That("a short code is refused", !PairingCode.LooksValid("123"));
        Check.That("a long one is refused too", !PairingCode.LooksValid("1234567"));

        // Two codes in a row being identical would mean the generator is broken; over a
        // handful of draws it is vanishingly unlikely by chance.
        var drawn = new HashSet<string>();
        for (int i = 0; i < 20; i++) drawn.Add(PairingCode.Generate());
        Check.That("codes are not repeated draw after draw", drawn.Count > 15);

        var identity = DeviceIdentity.Create();
        var session = new PairingSession(identity.PublicKey);

        Check.That("a session starts with a full allowance",
            session.AttemptsRemaining == PairingCode.MaxAttempts);
        Check.That("and its code is valid", PairingCode.LooksValid(session.Code));

        string first = session.Code;

        Check.That("a wrong answer does not immediately replace the code",
            !session.RecordFailure());
        Check.That("but it is counted",
            session.AttemptsRemaining == PairingCode.MaxAttempts - 1);
        Check.That("and the code has not changed yet", session.Code == first);

        bool rotated = false;
        for (int i = 1; i < PairingCode.MaxAttempts; i++) rotated |= session.RecordFailure();

        Check.That("running out of attempts replaces the code", rotated);
        Check.That("the new code is different", session.Code != first);
        Check.That("and the allowance is restored for it",
            session.AttemptsRemaining == PairingCode.MaxAttempts);
        Check.That("the rotation is counted", session.Rotations == 1);

        // A typo before a correct answer must not leave the user one mistake from a
        // rotation for the rest of the session.
        session.RecordFailure();
        session.RecordSuccess();
        Check.That("a success clears the failure count",
            session.AttemptsRemaining == PairingCode.MaxAttempts);

        // The secret must follow the code currently on screen, or a rotation would leave
        // the PC checking against something nobody can see.
        var other = new PairingSession(identity.PublicKey);
        byte[] before = other.Secret();
        string wasShowing = other.Code;

        while (!other.RecordFailure()) { }

        byte[] after = other.Secret();
        Check.That("the secret changes when the code does", !before.SequenceEqual(after));
        Check.That("and matches whatever is on screen now",
            after.SequenceEqual(PairingCode.ToSecret(other.Code, identity.PublicKey)));
        Check.That("the old code no longer works",
            !after.SequenceEqual(PairingCode.ToSecret(wasShowing, identity.PublicKey)));

        // Same code, different PC, different secret: a code overheard near one machine is
        // useless against another.
        var elsewhere = DeviceIdentity.Create();
        Check.That("a code is bound to the PC that offered it",
            !PairingCode.ToSecret("123456", identity.PublicKey)
                .SequenceEqual(PairingCode.ToSecret("123456", elsewhere.PublicKey)));
    }

    private static void Identity()
    {
        Check.Section("14. Device identity");

        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();

        Check.That("a generated identity has a public key", pc.PublicKey.Length > 0);
        Check.That("two devices get different identities",
            !pc.PublicKey.SequenceEqual(phone.PublicKey));

        // Both sides must reach the same shared secret, or nothing else works.
        byte[] fromPc = pc.Agree(phone.PublicKey);
        byte[] fromPhone = phone.Agree(pc.PublicKey);
        Check.That("both sides agree on the same shared secret",
            fromPc.SequenceEqual(fromPhone));
        Check.That("the shared secret is a full-length key", fromPc.Length >= 32);

        // A third device must not land on that secret.
        var stranger = DeviceIdentity.Create();
        Check.That("an unrelated device derives a different secret",
            !stranger.Agree(pc.PublicKey).SequenceEqual(fromPc));

        // Identity must survive being stored and restored.
        byte[] stored = pc.ExportPrivateKey();
        var restored = DeviceIdentity.Import(stored);
        Check.That("identity survives export and import",
            restored.PublicKey.SequenceEqual(pc.PublicKey));
        Check.That("fingerprint is stable across a restore",
            restored.Fingerprint == pc.Fingerprint);

        Check.That("fingerprint is human-checkable (4 groups of 4 hex)",
            pc.Fingerprint.Split(' ').Length == 4 &&
            pc.Fingerprint.Split(' ').All(g => g.Length == 4));
        Check.Note($"example fingerprint: {pc.Fingerprint}");
    }

    private static void Offer()
    {
        Check.Section("15. Pairing QR payload");

        var pc = DeviceIdentity.Create();
        var offer = PairingOffer.Create(pc, "Study PC", port: 47820, addresses: new[] { "192.168.1.40" });

        string qr = offer.ToQrPayload();
        Check.Note($"QR payload is {qr.Length} characters");
        Check.That("QR payload fits comfortably in a scannable code", qr.Length < 400);

        var scanned = PairingOffer.FromQrPayload(qr);
        Check.That("a scanned payload parses back", scanned is not null);
        Check.That("public key survives the round trip",
            scanned!.PublicKeyBytes.SequenceEqual(pc.PublicKey));
        Check.That("pairing secret survives the round trip",
            scanned.PairingSecretBytes.SequenceEqual(offer.PairingSecretBytes));
        Check.That("both sides compute the same fingerprint",
            scanned.Fingerprint == pc.Fingerprint);

        // The whole point: this must be safe to photograph in the sense that it
        // reveals nothing stored in the vault.
        Check.That("the payload contains no vault path",
            !qr.Contains("Lockwell", StringComparison.OrdinalIgnoreCase));
        Check.That("the pairing secret is fresh on every offer",
            !PairingOffer.Create(pc, "Study PC", 47820).PairingSecretBytes
                .SequenceEqual(offer.PairingSecretBytes));

        Check.That("a fresh offer has not expired", !offer.HasExpired);
        var stale = new PairingOffer
        {
            PublicKey = offer.PublicKey,
            PairingSecret = offer.PairingSecret,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
        };
        Check.That("an offer past its expiry is rejected", stale.HasExpired);

        Check.That("garbage does not parse as an offer",
            PairingOffer.FromQrPayload("not-a-real-payload") is null);
        Check.That("an empty payload does not parse",
            PairingOffer.FromQrPayload("") is null);
    }

    private static void KeyDerivation()
    {
        Check.Section("16. Transport key derivation");

        var pc = DeviceIdentity.Create();
        var phone = DeviceIdentity.Create();
        var pcEphemeral = DeviceIdentity.Create();
        var phoneEphemeral = DeviceIdentity.Create();

        byte[] psk = RandomNumberGenerator.GetBytes(32);

        // Both sides build the same transcript from the same public material.
        byte[] transcript = SyncProtocol.MixHash(
            SyncProtocol.InitialTranscript(),
            pc.PublicKey, phone.PublicKey, pcEphemeral.PublicKey, phoneEphemeral.PublicKey, psk);

        // ...and contribute the same shared secrets, from opposite directions.
        var initiator = SyncProtocol.DeriveTransportKeys(transcript,
            phoneEphemeral.Agree(pcEphemeral.PublicKey),
            phone.Agree(pc.PublicKey),
            psk);

        var responder = SyncProtocol.DeriveTransportKeys(transcript,
            pcEphemeral.Agree(phoneEphemeral.PublicKey),
            pc.Agree(phone.PublicKey),
            psk);

        Check.That("both sides derive the same send key",
            initiator.InitiatorToResponder.SequenceEqual(responder.InitiatorToResponder));
        Check.That("both sides derive the same receive key",
            initiator.ResponderToInitiator.SequenceEqual(responder.ResponderToInitiator));
        Check.That("the two directions use DIFFERENT keys",
            !initiator.InitiatorToResponder.SequenceEqual(initiator.ResponderToInitiator));
        Check.That("keys are full length",
            initiator.InitiatorToResponder.Length == SyncProtocol.KeySize);

        // Any tampering with the transcript must break agreement.
        byte[] tampered = SyncProtocol.MixHash(transcript, Encoding.UTF8.GetBytes("x"));
        var attacker = SyncProtocol.DeriveTransportKeys(tampered,
            phoneEphemeral.Agree(pcEphemeral.PublicKey),
            phone.Agree(pc.PublicKey),
            psk);
        Check.That("a modified transcript derives different keys (tampering is caught)",
            !attacker.InitiatorToResponder.SequenceEqual(initiator.InitiatorToResponder));

        // The wrong PSK must not reach the same keys, which is what binds a session
        // to one specific QR code.
        var wrongPsk = SyncProtocol.DeriveTransportKeys(transcript,
            phoneEphemeral.Agree(pcEphemeral.PublicKey),
            phone.Agree(pc.PublicKey),
            RandomNumberGenerator.GetBytes(32));
        Check.That("a wrong pairing secret derives different keys",
            !wrongPsk.InitiatorToResponder.SequenceEqual(initiator.InitiatorToResponder));

        // Fresh ephemerals per session = forward secrecy.
        var laterEphemeral = DeviceIdentity.Create();
        byte[] laterTranscript = SyncProtocol.MixHash(
            SyncProtocol.InitialTranscript(),
            pc.PublicKey, phone.PublicKey, pcEphemeral.PublicKey, laterEphemeral.PublicKey, psk);
        var later = SyncProtocol.DeriveTransportKeys(laterTranscript,
            laterEphemeral.Agree(pcEphemeral.PublicKey),
            phone.Agree(pc.PublicKey),
            psk);
        Check.That("a later session derives entirely different keys (forward secrecy)",
            !later.InitiatorToResponder.SequenceEqual(initiator.InitiatorToResponder));

        // Nonces must never repeat under one key.
        var nonces = Enumerable.Range(0, 1000)
            .Select(i => Convert.ToHexString(SyncProtocol.NonceFor((ulong)i)))
            .ToList();
        Check.That("1000 frame nonces are all distinct", nonces.Distinct().Count() == 1000);
        Check.That("nonce is the right size for ChaCha20-Poly1305",
            SyncProtocol.NonceFor(0).Length == SyncProtocol.NonceSize);
    }
}
