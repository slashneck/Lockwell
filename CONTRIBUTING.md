# Contributing

Thanks for looking. A few things worth knowing before you spend time on a change.

## Ground rules

**Never weaken a documented guarantee quietly.** If a change makes a protection
narrower, say so in the pull request and update `docs/THREAT-MODEL.md` in the same
change. Overstated security is treated as a bug in this project.

**Vault compatibility is not negotiable.** People have real data in these files and
there is no cloud backup to fall back on. Any change to `Lockwell.Core/Crypto/`, the
vault format, or the JSON member names must prove that an existing vault still opens.
Add a check that proves it rather than testing by hand.

**The master password never becomes a `string`.** It travels as a `SecureString` to
the key derivation function and is wiped afterwards. `Argon2KeyDeriver.DeriveKey`
deliberately has no `string` overload. Please keep it that way.

**Dependencies are kept to a minimum**, especially anywhere near crypto. A pull
request that adds a package needs to argue for it.

## Before you open a pull request

```
dotnet run --project Lockwell.Tests -c Debug --nologo
```

Everything must pass. If you added a guarantee, add checks for it. The harness covers
the vault round trip, key derivation, the sync protocol, the update trust chain, and a
set of guards for bugs that reached the app once and are not allowed back.

Two of those guards catch mistakes that are easy to make again:

- **No `Effect` on anything holding text.** A blur or drop shadow on a container with
  text in it renders badly and has been reintroduced three times. The check exists so
  there is no fourth.
- **Every resource key referenced in XAML must exist.** `FindResource` throws at
  runtime on a missing key, which means a typo is a crash rather than a bad colour.

## Style

Match the file you are editing. The codebase favours comments that explain *why* a
thing is the way it is, especially where the obvious approach is wrong. Comments that
restate the code are not wanted.

## Building

The .NET 8 SDK is enough for the desktop app, the core library, the installer and the
tests. The Android companion also needs the MAUI workload.

```
dotnet build Lockwell/Lockwell.csproj -c Release
```

Release packaging lives in `build-tools/`. Signing a release needs the private key,
which is not in this repository, so release scripts are usable but the final signing
step is not reproducible outside the project.

## Licence

Contributions are accepted under GPL-3.0-or-later, the same licence as the project.
