# Lockwell for Android: threat model

Written in the same spirit as the desktop `THREAT-MODEL.md`: what the phone app protects
against, what it does not, and where the honest limits are. A security document that only
lists strengths is marketing.

The phone app is a vault in its own right, not a mirror of the PC. It works with no PC
involved; linking is one feature among several.

---

## 1. What it protects against

**Someone who picks up your phone without your password.**
Each vault has its own data key, wrapped by a key derived from that vault's password with
Argon2id (256 MiB, the same parameters as desktop). There is no stored password verifier
and no recovery backdoor. Without the password, or an enrolled fingerprint, the contents
are ciphertext.

**Another app on the phone.**
Everything lives in the app's private internal storage, which Android isolates per package.
Nothing is ever written to shared storage, `/sdcard`, or MediaStore.

**Cloud backup.**
`allowBackup="false"`, `fullBackupContent="false"` and explicit data-extraction rules in the
manifest. Android's automatic backup cannot sweep a phone vault into a Google account, and
device-to-device transfer cannot carry it to a new phone.

**Uninstalling.**
All data is inside the package's private directory, so uninstalling removes it completely.
Nothing is orphaned in shared storage for someone to find later.

**Screenshots and the task switcher.**
`FLAG_SECURE` is set, so the OS refuses screenshots and screen recording, and the thumbnail
Android caches for the app switcher is blank rather than a picture of an open vault.

**Someone watching the network while you transfer.**
Transfers are ChaCha20-Poly1305 inside a tunnel keyed by an ECDH exchange between the two
devices, with fresh ephemeral keys each session. A recorded transfer stays unreadable even
if both devices and their long-term keys are compromised later.

**Media leaking to the gallery.**
Photos, video and audio are decoded and played from memory. Nothing is written to a file to
be displayed, so the media scanner cannot index it, a gallery cannot list it, and a backup
cannot pick it up. See §4 for the one deliberate exception.

---

## 2. What it does NOT protect against

**Root, or a compromised phone.**
Root access defeats app-private storage the same way administrator access defeats anything
on Windows. A rooted or malware-bearing phone can read the app's files, and while a vault is
unlocked it can read the key out of memory. Nothing in this app changes that.

**Someone who knows your password.**
There is no second factor. The password, or an enrolled fingerprint that releases the
hardware-held key, is the whole of the authentication.

**Your keyboard.**
You type the vault password on a soft keyboard. Keyboards can learn words, keep clipboards,
sync to a vendor's cloud, and some are outright malicious. Lockwell cannot see what the
keyboard does with the characters it is given. A password manager's password is one of the
few things worth typing on a keyboard you trust.

**Anyone looking at your screen.**
`FLAG_SECURE` stops the software from capturing the screen. It does nothing about a camera
or a person standing behind you.

**Someone who already has the phone unlocked and the vault open.**
Auto-lock closes the vault when the app goes to the background or after idling, but an
attacker holding an unlocked phone with the vault open sees what you would see.

**Plaintext in memory.**
While an item is open its decrypted bytes exist in RAM. They are wiped when the viewer
closes and when the vault locks, but .NET's garbage collector may move objects, so a stale
copy can survive at an old address until overwritten. This is inherent to managed languages
and is not fixable from inside the app. It matters only to an attacker who can already read
the process's memory.

**Traffic analysis on your own network.**
Someone on your Wi-Fi cannot read a transfer, but can see that two devices talked, roughly
when, and roughly how much data moved.

---

## 3. Deleting things, and what "deleted" means

Worth stating plainly, because the intuition from desktop is wrong here.

**Overwriting files does not work on a phone.** Flash storage uses wear levelling: the
controller deliberately avoids writing the same physical cell twice, so "overwriting" a file
writes elsewhere and leaves the original cell holding the original data, unreachable from
software. File-shredding tools of the kind that make sense on a spinning hard drive give a
false sense of security on any phone or SSD.

**What protects it instead is that the whole device is encrypted.** Android has used
file-based encryption by default for years, so the remnants of a deleted file on the flash
are themselves ciphertext. Android also issues TRIM on delete, and the controller usually
erases those blocks in the background.

**The realistic leak is not the flash, it is the copies.** A photo that was in your gallery
before you put it in the vault may still exist in:

- Android's "Recently deleted", which keeps items for around 30 days
- thumbnail caches belonging to the gallery and any other app that displayed it
- the MediaStore database, which records names, sizes, dates and sometimes locations
- **a cloud service**, if the folder was ever synced. Deleting the local copy does nothing
  about the copy on someone else's server.

Lockwell offers to delete the phone's own copy after adding something to a vault, but
Android's photo picker usually hands the app a *copy* rather than the original, in which
case the app says so instead of deleting a scratch file and claiming success.

---

## 4. Deliberate exceptions

**"Save a copy" writes a real file.** That is what the action means. It goes to the app's
cache directory and is handed to the system share sheet. The entire cache is cleared on lock
and on launch.

That purge is wider than it looks, and deliberately so: an earlier version cleared only
Lockwell's own scratch folder and missed the copies the platform makes when a file is
shared, which left decrypted vault media on the phone across locks and restarts.

---

## 5. Linking, specifically

**The pairing code stops a stranger starting a pairing.** Six digits, with the code replaced
after three wrong answers so it cannot be guessed at online.

**The confirmation code proves who you connected to.** Both screens show six digits derived
from the finished handshake. Someone in the middle runs two separate handshakes and cannot
make both ends show the same number, so a mismatch means something is wrong. This is the
step that actually authenticates the pairing; the pairing code is not.

**Items are re-encrypted on arrival with the receiving device's own key.** The PC's data key
never crosses the wire. A lost phone therefore reveals nothing about the PC vault, which is
the entire reason the two devices do not share a key.

**A wipe cannot be guaranteed to reach a phone that never comes back.** Unpairing on the PC
removes the trust, and the phone acts on it the next time the two can see each other, but
there is no server, so there is no way to reach a device that is gone for good. The opt-in
dead-man's switch exists for exactly this, and it is honest about trusting the phone's own
clock.

---

## 6. Known gaps

Things that are true today and should not be discovered by surprise:

- **No mobile equivalent of the desktop preflight scanner.**
- **Clock trust.** Per-item expiry and the stale-device wipe use the phone's own clock.
  Someone holding the device can roll it back to stall them. Reconciling against PC time on
  the next successful sync bounds this to "buys delay", not "defeats it".
- **A large item is decrypted whole into memory** rather than streamed, so a very large video
  needs memory proportional to its size.
- **The MediaStore delete path is not implemented**, which is why deleting the phone's own
  copy of a gallery photo is currently reported as not possible rather than performed.
