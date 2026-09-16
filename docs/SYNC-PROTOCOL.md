# Lockwell sync protocol

Status: **design, not implemented.** This is the wire and key design for pairing and
transferring items between a Lockwell PC vault and a paired device. Written before any
code so the crypto is decided deliberately rather than improvised around a UI.

Companion to [MOBILE-COMPANION-PLAN.md](MOBILE-COMPANION-PLAN.md), which covers what
the app does and why. This document covers only *how the bytes move safely*.

Decisions locked in when this was written (2026-07-29): Android client is **.NET
MAUI**, so both ends are C# and the existing `Crypto/` folder is shared.

---

## 0. The one-sentence version

Two devices exchange long-term public keys by showing a QR code, then every later
connection is an authenticated, forward-secret tunnel over the local network; items
travel decrypted **inside that tunnel only**, and are immediately re-encrypted at rest
under the receiving device's **own** key, which the sender never learns.

---

## 1. Design rules this protocol must not break

Carried over from the desktop threat model and the mobile plan. If a change violates
one of these, the change is wrong.

1. **No server.** No relay, no rendezvous service, no push service, no STUN/TURN. If
   two devices cannot reach each other directly, the transfer does not happen.
2. **The PC's vault DEK never leaves the PC.** Not wrapped, not encrypted, not ever.
3. **Each device encrypts its own data at rest with its own key.** Compromising one
   device must not yield anything about the other's vault.
4. **The QR carries no vault data.** Only key material and pairing parameters.
5. **Nothing is transferred without explicit user action** on the sending device.
6. **Plaintext exists only in memory**, on both ends, and only while a transfer is
   actually running.

---

## 2. Cryptographic primitives

| Purpose | Algorithm | Where it comes from |
|---|---|---|
| Key agreement | X25519 | see §2.1 |
| Handshake pattern | Noise `XKpsk3` (pairing), `KK` (reconnect) | see §2.1 |
| Transport encryption | ChaCha20-Poly1305 | `System.Security.Cryptography` (built in) |
| Key derivation (session) | HKDF-SHA256 | `System.Security.Cryptography.HKDF` (built in) |
| Key derivation (password) | Argon2id | `Konscious.Security.Cryptography.Argon2` (already a dependency) |
| At-rest encryption | AES-256-GCM | `System.Security.Cryptography` (built in, already used) |
| Content hashing | SHA-256 | built in |
| Randomness | `RandomNumberGenerator` | built in, already the single source (`SecureRandom`) |

### 2.1 The one dependency question, stated honestly

.NET 8 has **no X25519**. `ECDiffieHellman` only offers the NIST curves. So there are
two viable routes and they should be chosen deliberately, not by accident:

**Route A (recommended): add one vetted crypto library** providing X25519 and a Noise
implementation. This is consistent with existing practice — Lockwell already accepts
`Konscious.Security.Cryptography.Argon2` as a third-party crypto dependency precisely
because hand-rolling a KDF would be worse. A handshake is the same category of thing.
An audited Noise implementation removes an entire class of protocol mistakes that are
very easy to make and very hard to notice.

**Route B (no new dependency): build the handshake on .NET primitives.** Use
`ECDiffieHellman` with `nistP256` instead of X25519, HKDF-SHA256 for the chaining key,
ChaCha20-Poly1305 for handshake and transport. Follow the Noise XK/KK *structure*
without claiming to be Noise. P-256 is perfectly sound cryptography — the tradeoff is
not curve strength, it's that the composition becomes ours to get right rather than
someone else's to have already got right.

**Recommendation: Route A.** The rule that matters for Lockwell is "minimal
dependencies, and any dependency in the crypto path must be vetted" — not "zero
dependencies at any cost." Argon2 already set that precedent. Everything below is
written to work either way; where it matters, the Route B substitution is noted.

---

## 3. Device identity

Every device — PC or phone — has a long-term **identity keypair** generated once, on
first run.

- **PC**: private key stored **inside the encrypted vault** (`VaultData`), so it is
  protected by the master password like everything else. It only needs to exist while
  the vault is unlocked, which is the only time syncing is possible anyway.
- **Phone**: private key stored in the **Android Keystore**, hardware-backed
  (StrongBox where available). The key material never enters app memory in the clear.

The public half is what travels in the QR and what the other device pins.

**Device fingerprint**: `SHA-256(publicKey)`, rendered as the first 8 bytes in groups
(e.g. `A4F2 91C7 3B08 5E1D`). Shown in the pairing UI on both devices so a user can
visually confirm they paired with the right thing. Cheap to display, and the only
defence against someone substituting a QR code.

---

## 4. Pairing

### 4.1 What is in the QR code

The PC generates a fresh pairing offer and renders it as a QR. Payload is CBOR or
compact JSON, then Base64url:

```
{
  "v":  1,                    // protocol version
  "pk": "<32 bytes>",         // PC's long-term identity public key
  "ps": "<32 bytes>",         // one-time pairing secret (PSK), random per QR
  "n":  "Study PC",           // PC's display name, for the phone's UI
  "ep": 1785312000,           // expiry, unix seconds — QR valid ~2 minutes
  "ad": ["192.168.1.40"],     // address hints, optional, speeds up first connect
  "pt": 47820                 // TCP port the PC is listening on
}
```

Roughly 120–160 bytes encoded. Comfortably inside QR version 6 at error correction M.

**The pairing secret is the whole authentication story.** Anyone who can see the
screen can pair — and that is the intended security model, exactly like Bluetooth
pairing codes. Physical sight of the display *is* the out-of-band authenticated
channel. Consequences:

- The QR **expires** (~2 minutes) and the PC stops accepting it after that.
- The QR is **single-use**: once a pairing completes, the PC discards that secret.
- The pairing screen must warn against screen-sharing or photographing the QR. This
  is the one moment in Lockwell's life where showing your screen to someone hands
  them real access, which is a nice irony given `SetWindowDisplayAffinity` — the
  pairing window should be **exempted from capture exclusion** so the user can
  actually scan it, and that exemption is worth an explicit note in the UI.

### 4.2 PC-to-PC (no camera)

Same payload, rendered as a typed code instead of a QR. To stay typable, the QR's
`ps` is replaced by a shorter **pairing PIN** and the payload is fetched over the
network:

1. Responder PC displays a 9-character Crockford Base32 PIN (matching the existing
   `RecoveryCode` alphabet — no ambiguous I/L/O/U) plus its address.
2. Initiator PC types the PIN.
3. Both derive `PSK = Argon2id(PIN, salt = SHA-256(responderPublicKey))` with
   deliberately heavy parameters (the desktop defaults: 256 MiB, 4 passes), which makes
   guessing a short PIN prohibitively slow even though the PIN itself is low entropy.
4. Handshake proceeds identically to §4.3.

The PIN is short enough to type and, because Argon2id gates each guess, the low
entropy is acceptable for a code that lives for two minutes on a local network.
Responder must rate-limit and abandon the pairing after ~5 failed attempts.

### 4.3 The pairing handshake

Pattern: **`Noise_XKpsk3_25519_ChaChaPoly_SHA256`**

Why this pattern:
- **X** — the initiator (phone) transmits its own static key, encrypted, so it stays
  private to eavesdroppers while still authenticating the phone for future sessions.
- **K** — the responder's (PC's) static key is already **K**nown to the initiator,
  because it came from the QR. This is what stops a substituted PC from answering.
- **psk3** — the pairing secret is mixed in at message 3, binding the entire session
  to *this specific QR code*. Without it, anyone on the network who learned the PC's
  public key could attempt to pair.

*(Route B substitution: same three-message structure, P-256 ECDH in place of X25519,
PSK mixed into the chaining key via HKDF at the same point.)*

```
Phone (initiator)                                  PC (responder)
  |  e                                                    |     msg 1
  |------------------------------------------------------>|
  |                                       e, ee, s(enc)    |     msg 2
  |<------------------------------------------------------|
  |  s(enc), se, psk                                       |     msg 3
  |------------------------------------------------------>|
```

On success both sides hold the same **handshake hash**. Both UIs display the device
fingerprint derived from it, and **pairing requires explicit confirmation on both
devices** — matching the "syncing a device doesn't happen at once but needs to be
confirmed on both devices" requirement. Neither side writes anything to its trust
store until both have confirmed.

### 4.4 What gets stored on success

Each side appends to its trust store:

```
{
  "peerPublicKey":  "<32 bytes>",
  "peerName":       "Gaming Phone",     // user-renameable
  "fingerprint":    "A4F2 91C7 3B08 5E1D",
  "pairedUtc":      "2026-07-29T18:22:11Z",
  "lastSeenUtc":    "2026-07-29T18:22:11Z",
  "expiryDays":     30,                 // 0 = never expire
  "wipeOnExpiry":   false               // the dead-man's switch, opt-in
}
```

- **PC**: stored inside the encrypted vault. Who you have paired with is exactly the
  kind of metadata that belongs behind the master password, matching the earlier
  decision to move the backup timestamp out of plaintext `settings.json`.
- **Phone**: stored in its own encrypted store, under the phone's own key.

---

## 5. Reconnecting

Once paired, no QR is ever needed again.

**Discovery**: mDNS/DNS-SD, service type `_lockwell._tcp.local`. Advertised TXT record
carries only the protocol version and a **rotating, non-identifying** device handle —
never the device name, never the fingerprint, never anything that identifies the vault
to a passive observer on a shared network (a café, an office, a dorm). Peers are
matched by completing the handshake, not by trusting anything in the advertisement.

**Handshake**: **`Noise_KK_25519_ChaChaPoly_SHA256`** — both static keys are already
known from the trust store, so mutual authentication needs no PSK and no user
interaction. Fresh ephemerals every session, so every session is forward-secret: a
device compromised tomorrow does not decrypt a transfer recorded today.

**A peer whose static key is not in the trust store is refused**, silently. No error
detail on the wire — an unpaired scanner learns only that something declined.

**USB fallback** runs the *identical* handshake over an ADB-forwarded local socket.
Same crypto, different pipe. USB proximity is not treated as authentication.

---

## 6. Transport

After either handshake, both sides hold two ChaCha20-Poly1305 keys (one per
direction) and a per-direction 64-bit counter.

**Framing**: `[uint32 length][ciphertext]`, length capped at 1 MiB per frame.

**Nonce**: the direction's counter, big-endian, left-padded to 12 bytes. Counters
never reset within a session, so a nonce is never reused under a given key. A session
is torn down and re-handshaked well before any counter could wrap.

**Large files are chunked** into ≤1 MiB frames, each independently authenticated. Each
chunk carries its index, and the receiver verifies the sequence is contiguous — so a
truncated or reordered transfer fails loudly rather than producing a corrupt file, the
same principle as the vault's own GCM tags.

**Every file also carries a SHA-256 of its plaintext**, verified after reassembly.
Belt and braces over the per-frame tags, and it doubles as the change-detection hash in
§8.

---

## 7. Keys at rest, on each side

This is the part that makes losing a phone survivable, and it deliberately reuses the
envelope design already in `VaultHeader`.

**The phone has its own vault.** Not a copy of the PC's — its own, with its own DEK.

```
phone password --Argon2id(salt)--> PhoneKEK
PhoneDEK = 32 random bytes, generated on the phone, never transmitted

PhoneDEK --AES-256-GCM(PhoneKEK)--------------> passwordWrappedKey
PhoneDEK --AES-256-GCM(KeystoreKey)-----------> biometricWrappedKey  (optional)
```

**Biometrics wrap the same DEK — they never replace it.** Fingerprint or face unlock
authorises use of a hardware-backed Keystore key, which unwraps the DEK. The
biometric is a gate on a key, never a substitute for one. Turning biometrics off
simply deletes `biometricWrappedKey`; the password path is untouched. *(This is the
single easiest thing to implement backwards and the mistake would be invisible in
testing — it "works" either way.)*

**A transfer, end to end:**

1. PC decrypts the item with the **PC's** DEK, in memory.
2. Plaintext travels inside the ChaCha20-Poly1305 tunnel. Never on disk on either side.
3. Phone re-encrypts with the **phone's** DEK and writes it to its own store.
4. Plaintext buffers on both sides are zeroed (`CryptographicOperations.ZeroMemory`),
   matching existing desktop discipline.

Neither DEK ever crosses the wire. A stolen phone yields only what was sent to that
phone, under a key with no relationship to the PC's master password.

---

## 8. Item state and change detection

The PC tracks, **inside its encrypted vault**, per paired device:

```
{
  "deviceId":     "<fingerprint>",
  "itemId":       "<entry or attachment id>",
  "sentUtc":      "2026-07-29T18:40:02Z",
  "sentHash":     "<sha256 of the plaintext that was sent>",
  "optimized":    true,          // a mobile-optimised copy, not the original
  "expiresUtc":   null           // per-item expiry, §9
}
```

- **"Needs Update"** = current plaintext hash ≠ `sentHash`. Cheap, exact, no clocks
  involved.
- **Already transferred and unchanged** = skipped, never re-sent.
- **"Available on Phone" removed** = queued as a delete instruction on next connect.
- When Mobile Optimization is on, `sentHash` is the hash of **what was actually sent**
  (the optimised copy), so re-optimising with the same settings doesn't falsely read as
  changed. The desktop `IsWorthwhile` guard applies unchanged: a file that would not
  get meaningfully smaller is sent as-is.

---

## 9. Expiry and wipe enforcement

Two independent mechanisms, both **opt-in and off by default** (§7–8 of the plan doc).

**Per-item expiry.** `expiresUtc` is set by the sender and travels with the item. The
phone enforces it locally — no network needed, no pairing state involved. Items show a
visible countdown so deletion never looks like a bug.

**Dead-man's switch.** The phone records `lastSuccessfulSyncUtc`. If
`now - lastSuccessfulSyncUtc > deadManInterval`, the phone deletes every item that was
**received from a peer**, and only those. Items that originated on the phone are never
touched by any wipe path — *never destroy the only copy of something as a security
measure.*

**Clock trust, stated honestly.** Both mechanisms trust the phone's own clock, and
someone holding the device can roll it back. There is no local fix. The mitigation:
on every successful reconnect the phone receives the PC's authoritative time and
**reconciles immediately**, deleting anything that should already have expired. This
bounds the attack to "buys delay while physically holding the device," not "defeats it
outright." Both the UI and the eventual mobile threat model should say so plainly
rather than implying a guarantee.

**Unpair does not wipe by default.** Removing a device leaves its items in place
unless `wipeOnExpiry` is on. A phone that was merely off for three weeks is
indistinguishable from a stolen one, and quietly destroying holiday photos because
someone travelled would be a worse failure than the one being defended against.

---

## 10. What an attacker on the same network gets

Worth stating, in the spirit of the existing threat model's honesty about metadata.

**Cannot get**: item contents, filenames, vault structure, either DEK, the master
password, or anything from a recorded session later (forward secrecy).

**Can observe**: that a Lockwell device exists on the network, roughly when syncs
happen, and the approximate size and count of transferred items from traffic volume.
Padding could hide sizes; it is not planned for v1 because the cost is real and the
threat is modest for a home network. **This is a deliberate accepted limitation, not
an oversight** — same category as the vault file's attachment-size metadata.

**Cannot do**: replay a captured session (fresh ephemerals, counter nonces), pair
without seeing the screen (PSK), or impersonate a paired device without its private
key (which on the phone is hardware-backed).

---

## 11. Versioning

The QR and both handshakes carry a protocol version. Devices refuse to talk across
incompatible versions and say so plainly, naming which side is older. The trust store
records the version negotiated at pairing, so an app update can detect an
out-of-date peer before attempting a transfer rather than failing mid-way.

---

## 12. Android packaging, signing and updates

Not cryptography, but it belongs here because one mistake is permanent.

### 12.1 Signing

Every APK must be signed, and **Android will only install an update over an existing
app if it is signed with the same key.**

> **Lose the signing keystore and the app can never be updated again.** Every existing
> user would have to uninstall — destroying their phone vault — and start over.

Therefore:

- Generate the keystore **once**, before the first release.
- **Back it up the way the recovery key is backed up**: offline, more than one
  location, never in the repo. Add `*.jks` / `*.keystore` to `.gitignore` before the
  first one is created, not after.
- Use APK Signature Scheme **v2 and v3** (v3 permits future key rotation; v1-only
  would foreclose that).
- `versionCode` increases monotonically. Android refuses to install a lower one.

### 12.2 Distribution and patching

There is no installer program on Android — **the APK is the installer**, and installing
a newer APK over an older one upgrades in place while keeping app data.

The PC's "Mobile" section shows a QR linking to the current APK, per the spec. Two
honest notes:

- That download comes over the internet (GitHub Releases, like the existing Windows
  installer). **This is software distribution, not vault data.** It is the one place
  the "nothing leaves your PC" line legitimately does not apply, and the UI should be
  clear that it is fetching an app, not syncing a vault.
- **In-app updating requires `REQUEST_INSTALL_PACKAGES`**, which Android treats as
  high-risk and prompts loudly about. It cannot be silent. For a security product,
  the loud prompt is arguably correct — but plan for it rather than discovering it
  late.

### 12.3 Manifest requirements

Non-negotiable for this app:

```xml
android:allowBackup="false"        <!-- keeps the vault out of Google's cloud backup -->
android:fullBackupContent="false"
android:dataExtractionRules="..."  <!-- API 31+, exclude everything -->
```

Plus `FLAG_SECURE` on every window showing vault content — the nearest Android
equivalent to the desktop's capture exclusion, and weaker, which the mobile threat
model should say.

---

## 13. Build order

Deliberately sequenced so the dangerous parts are proven before anything depends on
them.

1. **Crypto core, shared.** Move `Crypto/` into a shared project both apps reference.
   Verify the existing vault harness still passes unchanged.
2. **Handshake, headless.** Pairing and reconnect between two console processes on one
   machine. No UI, no phone, no network stack. Test: mutual auth succeeds, wrong PSK
   fails, expired QR fails, unknown peer refused, replayed session refused.
3. **Transport, headless.** Framing, chunking, hash verification, truncation and
   reorder failures.
4. **USB transport.** ADB-forwarded socket, real phone, still no UI. Proves the whole
   pipeline before discovery complexity exists.
5. **mDNS discovery.** The part most likely to fight the network. Real device, real
   WiFi.
6. **UI, both sides.** Pairing pages, trusted devices, transfer queue, "Available on
   Phone".
7. **Expiry and wipe.** Last, because it destroys data and everything else must be
   trustworthy first.

Steps 1–3 need no Android toolchain at all and are testable with the harness pattern
already in use.

---

## 14. Still open

- Route A vs Route B (§2.1) — needs a decision before step 2.
- Which Noise library, if Route A.
- mDNS library for MAUI Android, or hand-rolled UDP multicast.
- Whether the PC should also enforce expiry on its own record of what a device holds,
  or trust the phone to report.
- Conflict handling when the same item is edited on both devices while disconnected.
  Low frequency given the opt-in model, but undefined.
- Whether transfer sizes warrant padding (§10) in a later version.
