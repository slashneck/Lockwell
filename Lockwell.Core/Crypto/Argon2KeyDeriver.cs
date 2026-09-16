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

using System.Security;
using Konscious.Security.Cryptography;

namespace Lockwell.Crypto;

/// <summary>
/// Turns a human master password into a 32-byte key using Argon2id.
///
/// Why Argon2id: it is memory-hard, so an attacker who steals the vault file
/// cannot cheaply brute-force the password on GPUs/ASICs. This is the modern
/// standard recommended by the Password Hashing Competition and OWASP.
///
/// The parameters are stored in the vault header (they are not secret). That lets
/// us raise the cost later without breaking old vaults: each vault remembers the
/// settings it was created with.
/// </summary>
public sealed class Argon2Parameters
{
    /// <summary>Memory cost in kibibytes. 256 MiB by default.</summary>
    public int MemoryKib { get; init; } = 256 * 1024;

    /// <summary>Number of passes over memory.</summary>
    public int Iterations { get; init; } = 4;

    /// <summary>Degree of parallelism (lanes).</summary>
    public int Parallelism { get; init; } = 2;

    /// <summary>Per-vault random salt.</summary>
    public required byte[] Salt { get; init; }

    public static Argon2Parameters CreateDefault()
        => new() { Salt = SecureRandom.GetBytes(16) };
}

public static class Argon2KeyDeriver
{
    public const int KeyLengthBytes = 32; // 256-bit key for AES-256

    /// <summary>
    /// Derive the key-encryption-key from the master password.
    /// The password bytes are zeroed as soon as derivation finishes.
    ///
    /// Takes a <see cref="SecureString"/> rather than a string on purpose: there is no
    /// string overload, so no caller can accidentally leave the password sitting in the
    /// managed heap where it could never be wiped.
    /// </summary>
    public static byte[] DeriveKey(SecureString password, Argon2Parameters p)
    {
        byte[] passwordBytes = SecurePassword.ToUtf8Bytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = p.Salt,
                MemorySize = p.MemoryKib,
                Iterations = p.Iterations,
                DegreeOfParallelism = p.Parallelism,
            };
            return argon2.GetBytes(KeyLengthBytes);
        }
        finally
        {
            Array.Clear(passwordBytes, 0, passwordBytes.Length);
        }
    }
}
