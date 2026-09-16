# Lockwell vault format

This document describes exactly how Lockwell stores data on disk, so anyone can
verify what is and is not protected. There are no hidden formats and no network
component.

## Files on disk

Default location: `%LocalAppData%\Lockwell\`

```
Lockwell\
  vault.lwv            Encrypted vault (header + all entries/categories)
  settings.json        Non-secret preferences (NOT encrypted, contains no secrets)
  attachments\
    <id>.lwa           One encrypted blob per attachment (photo/video/file)
```

If `vault.lwv` and the `attachments` folder are copied to another machine, they
are useless without the master password (or recovery key). That is the whole
point.

## Cryptography

| Purpose | Algorithm |
|---|---|
| Key derivation (password to key) | Argon2id |
| Symmetric encryption | AES-256-GCM (authenticated) |
| Randomness | OS CSPRNG (`RandomNumberGenerator`) |

### Argon2id parameters (defaults)

- Memory: 256 MiB
- Iterations: 4
- Parallelism: 2
- Salt: 16 random bytes per vault

Parameters are stored in the vault header (they are not secret) so the cost can
be raised in future versions without breaking existing vaults.

### Envelope encryption

Lockwell does not encrypt your data directly with your password. Instead:

```
master password --Argon2id(salt)--> KEK_password (32 bytes)
recovery key (32 random bytes) -----> KEK_recovery   (optional)

DEK = 32 random bytes  (the Data Encryption Key)

vault contents  --AES-256-GCM(DEK)-->        header.Data
DEK             --AES-256-GCM(KEK_password)--> header.PasswordWrappedKey
DEK             --AES-256-GCM(KEK_recovery)--> header.RecoveryWrappedKey (optional)
```

Why this design:

- **Change password without re-encrypting everything.** Only the small wrapped
  key is rewritten; the bulk data stays as is.
- **Optional recovery** without a master backdoor. The recovery key is full
  entropy and is used directly as a key, never stored in plaintext.
- **No password verifier is stored.** Whether a password is correct is proven by
  AES-GCM authentication succeeding, so there is no separate hash to attack.

## `vault.lwv` structure

`vault.lwv` is a UTF-8 JSON header. All binary values are Base64.

```json
{
  "Magic": "LOCKWELL",
  "Version": 1,
  "Kdf": {
    "Algorithm": "argon2id",
    "MemoryKib": 262144,
    "Iterations": 4,
    "Parallelism": 2,
    "Salt": "<base64 16 bytes>"
  },
  "PasswordWrappedKey": "<base64 blob>",
  "RecoveryWrappedKey": "<base64 blob | null>",
  "Data": "<base64 blob>"
}
```

## Encrypted blob layout

Every blob produced by `VaultCrypto` (the wrapped keys, `Data`, and each `.lwa`
attachment) has the same layout:

```
[ nonce: 12 bytes ][ GCM tag: 16 bytes ][ ciphertext: N bytes ]
```

A fresh random nonce is generated for every encryption, so encrypting the same
content twice never produces the same bytes. The GCM tag means any tampering
(even a single flipped bit) makes decryption fail loudly instead of returning
corrupted data.

## Decrypted contents (`Data`)

Once decrypted, `Data` is JSON of the vault model:

```json
{
  "SchemaVersion": 1,
  "Categories": [ { "Id", "Name", "Glyph", "Order", "IsMedia" } ],
  "Entries": [
    {
      "Id", "CategoryId", "Kind", "Title", "Favorite",
      "Fields": [ { "Label", "Value", "IsSecret" } ],
      "Notes",
      "Attachments": [ { "Id", "FileName", "MediaType", "SizeBytes", "AddedUtc" } ],
      "CreatedUtc", "ModifiedUtc"
    }
  ]
}
```

This decrypted form exists only in memory while the vault is unlocked. Locking
the vault clears the data key and this model from memory.
