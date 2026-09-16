# Lockwell Mobile Companion — design discussion record

Status: **discussed, not built.** Nothing in this document exists in code yet. This is
a record of what was decided in planning conversations, so the reasoning survives even
if the conversation that produced it doesn't. Written 2026-07-29.

Original spec that kicked this off: `Lockwell Mobile Companion App Specification.pdf`
(user's Downloads folder). This document extends and, in a couple of places, corrects
that spec based on follow-up discussion.

---

## 1. What this is, in one paragraph

A separate Android app — not a port of the Windows app, a new project that shares
Lockwell's crypto philosophy. The phone is never a mirror of the PC vault. It holds
only the individual items you explicitly choose to send it. Pairing is local-only, no
server ever exists, and the whole thing works or fails on that constraint: if it can't
be done without a server, it doesn't get done.

Platforms: Windows (existing) and Android (APK sideload initially, Play Store maybe
later). No iOS — sideloading is too much friction on that platform to be worth it yet.

---

## 2. Why a separate app, not a port

Lockwell's desktop security depends on WPF-adjacent, Windows-specific plumbing:
`SetWindowDisplayAffinity` for capture exclusion, `GetLastInputInfo` for idle
detection, the whole DPAPI-adjacent assumption of "local" baked into how the app
thinks. None of that exists on Android. So this was never going to be a port.

What **does** port cleanly: the `Crypto/` folder. Argon2id and AES-256-GCM are pure
.NET with no Windows dependency — they were already portable. If the Android client
ends up being .NET MAUI rather than native Kotlin, that folder could move over close
to unmodified. Native Kotlin + Jetpack Compose is the other realistic option; the
tradeoff is reuse (MAUI) versus a more idiomatic, better-supported Android surface
(native). Not decided.

---

## 3. Threat model changes once two devices exist

The desktop threat model's central promise is "nothing leaves your PC." The moment two
devices sync, something crosses a wire, even if that wire never leaves the local
network. That crossing needs its own explicit design, not an assumption inherited from
the desktop model. Specific new risks a phone introduces that a PC mostly doesn't:

- Clipboard managers and keyboard prediction that can retain what you typed.
- OS-level screenshot caching in the task switcher.
- Cloud backup services (Google's) that can silently snapshot app data unless the app
  explicitly opts out.
- No equivalent to `SetWindowDisplayAffinity` — Android's `FLAG_SECURE` exists but is
  weaker.
- Root access defeats app-private storage the same way admin access defeats anything
  on Windows; worth one honest line in a future mobile threat model, matching the tone
  of the desktop one.

None of this is a reason not to build it. It's a reason the mobile app needs its own
`THREAT-MODEL.md` eventually, written with the same honesty as the desktop one.

---

## 4. Device pairing

**Mechanism:** QR code on the PC, scanned by the phone. The QR carries **no vault
data** — only pairing/key material. Its only job is a secure exchange of long-term
public keys.

**PC-to-PC / PC-to-laptop pairing** (no camera on one end): a manually-typed code
instead of a QR scan. This still needs a defined exchange protocol — a short
authentication string (SAS) pattern, similar to WPA setup codes, so the code can't be
trivially guessed or replayed. Not designed in detail yet.

**Crypto for the handshake — the one place to not hand-roll anything.** Use an
established, audited pattern (the Noise Protocol Framework, or the approach Signal/
age use for out-of-band key exchange) rather than inventing a scheme. This is the
mobile-sync equivalent of "don't write your own AES" — the discipline the desktop app
already follows for its crypto primitives should extend here.

**After pairing, both devices are "trusted."**

---

## 5. Per-device keys — the architectural core of the whole security model

This is the single most important decision in the whole plan, and it was explicitly
flagged as a gap in the original spec (which only said "only Lockwell can decrypt
after authentication" without saying *whose* key).

**The phone must never receive a copy of the PC's actual vault DEK.** If it did, a
lost or compromised phone would be equivalent to a compromised PC vault — exactly what
the item-level "you choose what's on the phone" design is trying to prevent.

**The correct design, and it reuses architecture that already exists on desktop:**
each paired device gets its **own** DEK for the items it holds. That DEK is wrapped by
a phone-local key — derived from the phone's own password/PIN, or gated behind
biometrics via the Android Keystore/StrongBox (hardware-backed on most phones; key
material never leaves secure hardware). This is precisely the envelope-encryption
pattern desktop already uses (`PasswordWrappedKey` / `RecoveryWrappedKey` in
`VaultHeader`), applied per-device instead of per-vault.

Consequence: losing a phone costs you only the items that were on that phone,
encrypted with a key that has no relationship to the PC's master key.

**Fingerprint / biometric unlock:** confirmed as an option the user explicitly wants.
The correct mental model, stated plainly so it's never implemented backwards: **the
fingerprint gates access to a hardware-backed key. It never replaces the key.** Same
principle as desktop, where there is no backdoor and no stored password verifier —
whoever implements the Android side needs to internalize this before writing any auth
code, because "fingerprint unlocks the vault directly" is the wrong and much weaker
design that this phrase is easy to slide into by accident.

**Phone password is independent from the PC master password.** Confirmed by the user
explicitly: "the phone password should not be the same as the PC world... it's gonna
be a separate word." Set independently on first phone setup. Rationale (mine, offered
during discussion): a compromised phone PIN then can't be used to guess at the PC
vault.

---

## 6. Trusted devices list (PC side)

The PC maintains a list of paired devices, each showing:

- Device name (renameable)
- Last connected
- Online/offline status — only meaningful when discoverable on the local network
- Date paired

Actions available: rename, remove, disconnect immediately, view pairing info.

---

## 7. Pairing expiry — and the wipe idea that came out of discussing it

**Base behavior (from the original spec, essentially unchanged):** a trusted pairing
auto-expires after 30 days of inactivity. On expiry: remove the trust, require a new
QR pairing to reconnect. User can disable auto-expiry entirely if they want.

**The idea that came out of pushing on this:** what if losing the phone should also
wipe the items that were sent to it? This went through several iterations before
landing somewhere sound. Recording the reasoning, not just the conclusion, because the
wrong versions of this idea are tempting and worth remembering *why* they're wrong.

### Rejected version: "wipe whenever it touches any WiFi that isn't the PC's"

This was the first framing ("once that device has WiFi connection or anything, it just
wipes"). Explicitly thrown out once the flaw was pointed out: this would also wipe a
phone that's simply been carried somewhere without ever having been lost — the
mechanism can't distinguish "stolen" from "at the grocery store." Confirmed dropped by
the user: "we cannot do... if I disconnected from my PC, the phone will wipe. That's
good call. So throw that away."

### Rejected as the *only* mechanism: "push a wipe command from the PC"

Sounds like the obvious feature — unpair on PC, phone gets the memo, wipes. The honest
problem: **there is no way to deliver that command to a device that never again
touches a network the PC can also reach.** No server means no push channel. A thief
who takes the phone off your WiFi and never brings it back never receives anything.
This isn't a flaw to fix, it's a hard limit of the "no server, ever" constraint, and
it needs to be stated to users plainly rather than implied away — same spirit as the
desktop threat model refusing to overclaim what the preflight scanner can catch.

### What was settled on: two independent, complementary mechanisms

1. **Fast path (opportunistic, not guaranteed):** unpair or let expiry fire on the PC.
   *If* the phone later reconnects to a network the PC can also see, it receives the
   wipe signal immediately.

2. **Slow path (self-enforced, guaranteed even if the phone is gone for good):** an
   **opt-in, off-by-default** dead-man's switch. If the phone hasn't successfully
   checked in with its paired PC within a **user-configurable** interval, the phone
   wipes the relevant items itself — no network required at the moment it fires, no
   cooperation needed from wherever the phone currently is.

   This is a genuine extension of the existing 30-day pairing-expiry concept, not a
   separate system: expiry already exists; this makes expiry able to *also* trigger a
   local wipe, if the user has turned that on.

   Confirmed by the user as its own separate toggle, distinct from plain pairing
   expiry, and explicitly **off by default** — "it should be an option that is
   separately made, not on by default... I want the user to have, like, full power of
   the app." The interval is a separate, independently adjustable dial from the base
   30-day pairing-expiry timer (e.g., pairing itself could stay at a lenient default
   while a wipe-if-lost timer, once turned on, might reasonably default shorter).

**What actually gets wiped — confirmed, this is a real design rule, not a detail:**
only items whose master copy still exists safely on the PC. Anything that
*originated* on the phone (e.g., a photo taken directly into the vault via the
phone's camera, if that's ever supported) is never touched by any wipe mechanism,
because the phone might be its only copy — wiping it would be data loss, not
protection. Stated as a general principle worth keeping visible: **never destroy the
only copy of something as a security measure.**

**Two honest caveats to state to users when this ships, not hide:**

- **Clock trust.** Both the dead-man's switch and the per-item expiry (§8) trust the
  phone's own wall clock. Someone holding the device could disable auto-time or roll
  the clock back to stall expiry. Not fully fixable client-side. Mitigation: whenever
  the phone *does* successfully reach the PC again, re-check real expiry against PC
  time and catch up on anything that should already have fired. This bounds the
  attack to "buys delay while the attacker has physical possession," not "defeats it
  outright."
- **Can't distinguish "stolen" from "on a three-week trip with no network overlap."**
  Both look identical to a check-in-based mechanism. Mitigate via UX, not code: the
  toggle-on screen should say plainly what counts as a check-in, and the default
  interval should be generous (weeks, not days) unless the user tightens it
  deliberately.

---

## 8. Per-item expiry — separate feature, came out of the same conversation

Independent of the dead-man's switch. When sending an item to the phone, optionally
set it to auto-delete after a set time — "I send a picture to my phone, but set it to
wipe automatically in one day." Confirmed by the user as wanted **both** as a
per-transfer choice **and** as an optional general default the user can set once
("always expire phone transfers after 7 days unless I say otherwise") — "I'd say we do
both, but they're obviously off by default... I want the user to have full power of
the app." Manual-per-send should be the assumed default behavior when no general
default has been set, so it never surprises anyone.

Mechanically the cleanest of the two timers: the phone just knows "this item expires
at timestamp X" and deletes locally once its clock passes that point. No concept of
"checking in" required at all — works fully offline, independent of pairing state.
Same clock-trust caveat as §7 applies, and the same PC-time-reconciliation mitigation
on next successful reconnect.

UX note (not yet resolved): items with a pending expiry should visibly show something
like "expires in 18 hours" on the phone, so deletion never feels like an unexplained
disappearance. Matches the "lots of visual feedback" goal from the original spec.

---

## 9. Transfer model

- **Never a full mirror.** Every vault item optionally supports "Available on Phone."
  Only marked items transfer. Supported types: files, images, videos, audio,
  documents, entire folders.
- **Transfer Queue** on the PC side — items get queued, then sent as a batch when the
  user presses Transfer. Already-transferred files aren't re-sent unless changed;
  changed files show "Needs Update" and can be updated individually.
- **Direction:** both ways, PC → Phone and Phone → PC, over the local network once
  paired. USB exists as an explicit fallback transport.
- **Removing "Available on Phone"** removes the item from the phone on the next
  transfer.
- **No cloud requirement, ever.** Local-network auto-discovery between paired devices.
  Direct device-to-device transfer. No relay server, no login, no account — this is
  non-negotiable per the user's stated top priority.
- **All transfers end-to-end encrypted**, never plaintext on the wire. Files at rest
  on Android stay encrypted; only Lockwell (after phone-local authentication) can
  decrypt them, per the per-device-key model in §5.

---

## 10. Mobile Optimization (transfer-time compression)

An optional "Optimize Files for Mobile" setting on the PC's Transfer page, **off by
default**. If enabled:

- Images: resize if appropriate, compress while preserving quality.
- Video: reduce bitrate and/or resolution.
- Audio: optional bitrate reduction.
- Documents: optional compression where applicable.
- Already-compressed files generally pass through unchanged — same "don't degrade
  something for no gain" discipline as the desktop `MediaCompression.IsWorthwhile`
  guard (see `Services/MediaCompression.cs`), which already refuses to accept a
  re-encode that saves less than 5% or comes out larger.
- **The PC always keeps the original.** Only the phone receives the optimized copy.
  This mirrors desktop's existing "keep original, add smaller copy separately" default
  for single-item compression.

This is conceptually the same feature as desktop compression, applied at transfer
time instead of on demand. If it reuses `Windows.Media.Transcoding` and the WPF image
encoders the same way desktop does, it costs zero new dependencies on the PC side —
only the Android side needs its own (much simpler, since it only *receives*
already-optimized files and never has to encode anything itself).

---

## 11. Network discovery

Paired devices on the same local network should auto-discover each other (mDNS/NSD-
style). This is a genuine technical challenge for **testing**, not just shipping — see
§13.

---

## 12. Distribution

- Android app initially shipped as a sideloaded APK.
- Windows app gets a "Mobile" section where a QR code lets the user download the
  latest APK onto their phone.
- **Important distinction, worth stating explicitly so it's never confused with vault
  sync:** the APK download itself will almost certainly need the internet (e.g.
  GitHub Releases, the same way the existing Windows installer already fetches its
  package). That's a software-distribution trust boundary, completely separate from
  vault-data transfer, which stays local-only always. Don't let "no cloud" get
  interpreted as "the APK must download over local WiFi from the PC" — that was never
  the constraint; the constraint is that *vault data* never touches a server.
- Google Play support: possible later, not planned initially.

---

## 13. How this actually gets built and tested — dev environment plan

This came out of a practical question: how do we test an Android app when there's no
screen-share capability available to the assistant.

**The answer settled on: ADB (Android Debug Bridge) over USB, not screen mirroring.**
There's no way to consume a live video feed, but ADB gives something better for this
purpose:

- `adb install` — install builds directly, no manual sideloading per iteration.
- `adb logcat` — live crash/log output.
- `adb shell screencap` + pull — a real screenshot of the actual device state, readable
  as an image file.
- `adb shell input tap/text/...` — drive the UI directly, so testing can happen
  without a human tapping through each flow by hand.

**One-time setup needed on the phone:** enable Developer Options (tap Build Number
seven times under About Phone), turn on USB Debugging, accept the "trust this
computer" prompt on first connect.

**Important nuance for this project specifically:** USB gives *control*, not
*network*. Since pairing/discovery is WiFi-based, the phone also needs to be on the
**same WiFi as the PC** for that half of the feature to be testable — USB and WiFi are
two separate, complementary connections during dev, not substitutes for each other.

**Emulator vs. real device:** the Android Emulator (mentioned in the original spec) is
good for fast UI iteration but its networking is virtualized behind its own NAT by
default — it does **not** sit on the real LAN the way a physical device does, so
mDNS-style discovery between an emulator and the real PC won't work without extra
network bridging. Recommendation: emulator for screens/logic that don't depend on the
network; a real device (on real WiFi, connected via USB for control) for anything
touching pairing or discovery.

**Suggested build order, which follows directly from the above:** build and prove the
**USB-transport transfer path first.** It sidesteps discovery/mDNS entirely and is
fully testable through the same ADB connection already needed for driving the UI.
WiFi-based pairing and auto-discovery come second, once the core transfer/encryption
logic is already proven correct over USB.

---

## 14. Explicitly open / unresolved

Recorded so nothing gets silently assumed later.

**Resolved 2026-07-29** (see [SYNC-PROTOCOL.md](SYNC-PROTOCOL.md) for the detail):

- ~~MAUI vs. native Kotlin~~ → **.NET MAUI**, chosen so both apps stay in C# and the
  existing `Crypto/` folder is shared rather than reimplemented.
- ~~SAS/manual-code protocol for PC-to-PC pairing~~ → Crockford Base32 PIN, stretched
  with Argon2id against the responder's public key, rate-limited.
- ~~Which handshake pattern~~ → Noise `XKpsk3` for pairing, `KK` for reconnect.
- ~~Per-item expiry countdown UI~~ → yes, visible countdown, so deletion never reads
  as a bug.

**Still open:**

- Whether the handshake uses a vetted Noise library or is built on .NET primitives
  with P-256 — .NET 8 has no X25519. See SYNC-PROTOCOL.md §2.1. Needs deciding before
  the handshake is written.
- mDNS library for MAUI Android vs. hand-rolled UDP multicast.
- Conflict handling if an item is edited on both PC and phone while both were offline
  from each other — acknowledged as a real problem, not designed. Likely low-frequency
  given the selective/opt-in transfer model, but not zero.
- A mobile-specific `THREAT-MODEL.md`, written once the Android client exists, in the
  same honest style as the desktop one.
- Default interval for the dead-man's switch, and default interval for per-item
  expiry when the "always expire" default is turned on — user has final say, nothing
  proposed yet beyond "generous by default."

---

## 15. Standing constraints (do not relitigate without a good reason)

Restating these because they are the load-bearing walls of the whole plan, established
firmly across multiple messages:

- No server, ever, for anything touching vault data. Not a relay, not a sync service,
  not analytics. Software distribution (the APK download) is the one explicit
  exception, and only because it's not vault data.
- No account, no login, on either device.
- Phone vault items are individually opt-in — never a full mirror, never automatic.
- Every destructive/security action (bulk wipe, expiry) is opt-in and off by default;
  the user gets full manual control rather than the app deciding for them.
- Per-device encryption keys, never a shared master key crossing the wire.
- Fingerprint/biometric is always a gate on a real key, never a replacement for one.
- Never destroy the only copy of something as a "security" measure.
