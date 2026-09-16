# Security policy

## Reporting a vulnerability

Please report privately, not in a public issue.

Use GitHub's private reporting:
[Report a vulnerability](https://github.com/slashneck/Lockwell/security/advisories/new).
It creates a thread only the maintainers can see.

Useful things to include, as far as you have them:

- What an attacker gets, and what they need in order to get it
- The version, and whether it was an installed release or a build from source
- Steps to reproduce, or a proof of concept
- Anything that limits the impact

Please do not include your own vault data, key material, or anything from someone
else's machine.

## What to expect

Reports are read as soon as reasonably possible. Lockwell is maintained by a very
small team, so the honest answer on timing is: acknowledged in days, not hours.

If a report is valid, the fix ships in a release with the issue described in the
changelog. You will be credited by whatever name you ask for, or not at all if you
prefer.

## Scope

In scope, roughly in the order that matters:

- Anything that reveals vault contents without the master password or recovery key
- Weaknesses in key derivation, encryption, or the vault format
- Flaws in the update trust chain that could get unsigned or substituted code installed
- Flaws in device pairing or transfer that could leak items or accept an untrusted peer
- Secrets reaching disk, the clipboard, or a log where the documentation says they do not

Out of scope, because they are documented limits rather than bugs:

- Malware or a keylogger already running as your user. See `docs/THREAT-MODEL.md`.
- Recovering a decrypted video from `%TEMP%` while it is playing. Documented.
- Brute-forcing a weak master password.
- The absence of a recovery backdoor. That is the design.

If the documentation claims a protection that the code does not deliver, that is a
valid report. Overstated security is a bug here.

## Supported versions

The latest release. Lockwell is small enough that backporting to older versions would
be pretending at a process that does not exist.
