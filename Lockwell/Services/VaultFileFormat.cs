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

using System.Text.Json.Serialization;

namespace Lockwell.Services;

/// <summary>
/// The plaintext header written to <c>vault.lwv</c>. None of these values are
/// secret: the salt and KDF settings are meant to be public, and the wrapped
/// keys plus data are ciphertext that are useless without the master password.
///
/// Envelope encryption model:
///   masterPassword --Argon2id--> KEK_password
///   recoveryKey    ------------> KEK_recovery   (optional)
///   A random 32-byte Data Key (DEK) encrypts the actual vault contents.
///   The DEK is stored twice, each time wrapped (encrypted) by a KEK.
///
/// Why: it lets the user change their master password (re-wrap the same DEK)
/// without re-encrypting everything, and lets an optional recovery key open the
/// vault if the password is forgotten. There is no master backdoor key.
/// </summary>
public sealed class VaultHeader
{
    public string Magic { get; set; } = "LOCKWELL";
    public int Version { get; set; } = 1;

    public KdfSettings Kdf { get; set; } = new();

    /// <summary>DEK encrypted with the password-derived key. Base64.</summary>
    public string PasswordWrappedKey { get; set; } = "";

    /// <summary>DEK encrypted with the recovery key. Null if recovery disabled. Base64.</summary>
    public string? RecoveryWrappedKey { get; set; }

    /// <summary>The vault contents (VaultData JSON) encrypted with the DEK. Base64.</summary>
    public string Data { get; set; } = "";
}

public sealed class KdfSettings
{
    public string Algorithm { get; set; } = "argon2id";
    public int MemoryKib { get; set; }
    public int Iterations { get; set; }
    public int Parallelism { get; set; }
    public string Salt { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(VaultHeader))]
public partial class VaultHeaderJsonContext : JsonSerializerContext
{
}
