# Lockwell threat model

Honest security writing: what Lockwell protects against, and what it cannot. A
vault that overpromises is dangerous because it makes you careless.

## What Lockwell protects against (strong)

| Threat | Protected? | How |
|---|---|---|
| Someone copies your vault file | Yes | Data is AES-256-GCM encrypted; useless without the master password |
| Stolen laptop / drive (vault locked) | Yes | Nothing is readable without the password or recovery key |
| Tampering with the vault file | Yes | GCM authentication makes any edit fail to decrypt |
| Cloud/account breach | Yes (by design) | There is no Lockwell account and no server. Your vault is never uploaded anywhere |
| Brute-forcing a stolen file | Strongly slowed | Argon2id (256 MiB, memory-hard) makes guessing expensive |
| Casual snooping while you step away | Yes | Auto-lock on idle, on minimize; clipboard auto-clears |
| Screen share / recording the window | Mostly | Window is excluded from capture (OBS, Discord, recorders) |

## What Lockwell cannot fully protect against (be aware)

| Threat | Status | Why |
|---|---|---|
| Keylogger while you type the master password | Not solved | Malware running as you can read keystrokes. Preflight only catches known tools. |
| Master password sitting in a crash dump / page file | Mostly | The password is never held in a .NET string. See "How the master password is handled" below. |
| Malware while the vault is unlocked | Not solved | A process running as your user can read app memory / clipboard. |
| Decrypted video/audio on disk while playing | Partially | Unavoidable with the current media player. See "Playing video and audio" below. |
| Recovering a deleted scratch file from an SSD | Partially | Overwriting before delete is far less effective on SSDs than it sounds. See below. |
| A phone camera pointed at your screen | Not solved | Capture exclusion is an OS feature, not physical security. |
| Kernel-level / renamed spyware | Partially | The preflight scan checks known names; sophisticated malware hides. |
| Weak master password | Your responsibility | A stolen file + "password123" is crackable. Use a long passphrase. |
| Forgotten password with no recovery key | Not recoverable | By design. There is no backdoor. |
| A stolen release signing key | Not solved | Anyone holding it could sign an update that verifies. See "The update check" below. |
| GitHub learning your IP from the update check | Partially | Only if you leave the check on. It can be turned off in Settings. |

## The preflight scan: what it really is

The launch scan looks for **known** screen recorders and remote-access tools
(OBS, TeamViewer, AnyDesk, RustDesk, VNC, Bandicam, etc.) and warns you before
you type your password.

It is a **seatbelt, not a force field**:

- It cannot detect malware it does not have a name for.
- It cannot detect renamed or kernel-level keyloggers.
- A clean result means "nothing obvious found", not "you are safe".

Lockwell never silently continues when it finds something. It shows you the list
and makes you explicitly accept the risk.

## How the master password is handled

A subtle but real problem in most .NET apps: `string` is immutable and moved around
by the garbage collector, so a password that lands in one **can never be wiped**.
Copies can sit in the heap, a crash dump, or the Windows page file until the process
exits. Lockwell avoids that entirely.

The password travels as a `SecureString` from the text box all the way to the key
derivation function. It is decoded to UTF-8 bytes in unmanaged memory for the few
microseconds Argon2id needs, then both the unmanaged buffer and the byte array are
zeroed. `Argon2KeyDeriver.DeriveKey` deliberately has **no** `string` overload, so no
future code can accidentally reintroduce the leak. Password comparison (new password
vs confirmation) is constant-time and never materialises either value as a string.

Honest limit: while the password is briefly decoded it exists in memory, and malware
already running as your user could read it there. This closes the "left lying around
in the heap long after you typed it" window, not the "your machine is compromised"
one. Vault contents while unlocked remain the sensitive moment.

## Playing video and audio

Photos, GIFs, and gallery thumbnails are decrypted in memory and drawn directly. They
never touch the disk.

Video and audio are the exception. WPF's media player can only play from a *file
path*, and it cannot read from memory, so while a video plays, a decrypted copy exists
in `%TEMP%\Lockwell\`. That window cannot be closed without replacing the media stack
entirely, so it is instead made as small and as uninformative as possible:

- **Random file names.** A snapshot of `%TEMP%` reveals nothing about which vault
  items you opened. The names used to be the attachment ids themselves.
- **Deleted the moment you close the item, lock, or exit.**
- **Purged on startup too**, so a crash or power loss cannot leave a decrypted file
  sitting there until the next time you happen to lock.
- **Queued for deletion at reboot** if the player still holds the file open.

Honest limit: while a video is actually playing, that file is readable by any process
running as your user. If that matters for a particular file, it is safer not to play
it on a machine you do not fully trust.

## The update check

Earlier versions of this document said Lockwell made no network calls at all. That
stopped being true when the update checker was added, so here is what it actually does.

Once a day, a few seconds after you unlock, Lockwell asks GitHub whether there is a
newer release. It requests the release information for this project and nothing else.

**What leaves your machine:** an HTTPS request to `api.github.com`. That means GitHub
sees your IP address and the time you asked, the same as visiting the releases page in
a browser. Lockwell sends no identifier, no version number, no machine name, no
account, and nothing at all about your vault. The User-Agent is the bare word
"Lockwell" because GitHub rejects requests without one.

**Turning it off:** Settings, Updates, uncheck "Check for updates". Nothing then
contacts the network, ever. If you would rather Lockwell never had the option, build
it yourself with the check removed. The source is right here.

**Installing an update is the highest-risk thing Lockwell does.** It writes new
executable code onto your machine. Two things constrain it:

- Every release carries a manifest signed with a private key held offline. The
  matching public key is compiled into the app. A release that is not signed by that
  key is refused, whatever GitHub says about it, and whoever served the bytes.
- The check happens twice. The app verifies the signature, the size, and the SHA-256
  of the package before offering it, and the installer verifies all three again before
  writing a single file. The installer does not trust the app's word for it, because
  the installer is the process doing the writing.

Downloads only ever come from `github.com` and `githubusercontent.com` over HTTPS, and
only after you click. Lockwell never replaces itself silently while you are using it.

**Honest limits.** If the signing key were ever stolen, someone who could also control
what your machine downloads could sign a malicious update and it would verify. The key
is kept offline for exactly that reason, but "offline" is a practice, not a proof.
GitHub can also see that someone at your IP address runs Lockwell. If that alone is
unacceptable in your situation, turn the check off and update by hand.

## Source code and verifiability

Lockwell is free software under the GPL. Every claim on this page is checkable: the
Argon2 parameters, the fact that the master password never becomes a `string`, the
single network call, the absence of any backdoor. You do not have to take a security
promise on faith when you can read the code that makes it.

Honest limit: reading the source tells you what the source does, not what the
downloaded binary does. Release builds are obfuscated, which means the shipped files
do not correspond line for line with anything you can diff. If you want a binary you
can fully account for, build it yourself from this repository.

## About "secure" deletion

Scratch files are overwritten with random data before being deleted. On a mechanical
hard drive that genuinely destroys the old contents.

**On an SSD it is weaker than it sounds.** Wear levelling means the overwrite usually
lands on *different* physical flash cells than the original data, leaving the old
copy in unmapped blocks until the drive's garbage collector gets to it. The same
applies to copy-on-write filesystems and snapshots. Lockwell does the overwrite
because it costs little and helps on some hardware, but it is not a guarantee, and
no application can make it one. Full-disk encryption (BitLocker) is the real defence
here, because it protects those unmapped blocks too.

## Design rules Lockwell follows

These are commitments, verifiable in the source:

1. **One network call, and you can switch it off.** The update check is the only code
   in Lockwell that touches the network. There is no telemetry, no analytics, and no
   crash reporter. Nothing about your vault is ever sent anywhere. See "The update
   check" below for exactly what it does.
2. **No admin rights.** The app runs as a normal user (`asInvoker`). A vault
   asking for admin would be a red flag.
3. **Secrets stay in C#.** There is no browser/JS layer that could leak
   decrypted data.
4. **Minimal dependencies.** One vetted crypto library (Argon2id); everything
   else is the .NET base library. Fewer dependencies means fewer places for a
   supply-chain "trap" to hide.
5. **Crash safe.** An unhandled error closes the app rather than risk dumping
   secret-bearing state.
6. **Locking clears memory.** The data key and decrypted contents are wiped from
   memory on lock.
7. **The master password never becomes a string.** It stays in a `SecureString`
   until the moment it is hashed, so it can actually be wiped afterwards.
8. **Nothing decrypted is written to disk unless it has to be.** Only video and audio
   playback needs a file; everything else stays in memory. Decoded thumbnails are
   cached in memory only, and that cache is cleared when the vault locks.

## Your part of the deal

- Use a long, unique master password (a passphrase of several words is great).
- Save the recovery key offline (paper, or a separate encrypted device).
- Keep Windows and your antivirus updated.
- Do not unlock the vault on a machine you do not trust.
- Lock the vault (or let it auto-lock) when you walk away.

## Honest bottom line

Lockwell makes your stored secrets **unreadable at rest** and raises the bar a
lot versus keeping logins in Discord. The remaining weak points are mostly about
the live machine while you are using it (malware, cameras), which no local app
can fully eliminate. Treat the unlocked state as the sensitive moment.
