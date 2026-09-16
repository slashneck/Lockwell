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

namespace Lockwell.Crypto;

/// <summary>
/// Authenticated symmetric encryption for every secret Lockwell stores.
///
/// Algorithm: AES-256-GCM.
///   - Confidentiality: nobody without the key can read the data.
///   - Integrity/authenticity: if a single byte of the stored file is changed,
///     decryption fails loudly instead of returning garbage. So a "hacker who
///     gets to the file" cannot tamper with it undetected either.
///
/// Storage layout of every blob this class produces:
///     [ nonce: 12 bytes ][ tag: 16 bytes ][ ciphertext: N bytes ]
///
/// A fresh random nonce is generated for every single encryption, so encrypting
/// the same data twice never produces the same bytes.
/// </summary>
public static class VaultCrypto
{
    private const int NonceSize = 12; // 96-bit nonce, the GCM standard
    private const int TagSize = 16;   // 128-bit authentication tag

    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        ValidateKey(key);

        byte[] nonce = SecureRandom.GetBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[plaintext.Length];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize + TagSize, ciphertext.Length);
        return output;
    }

    /// <summary>
    /// Decrypt a blob produced by <see cref="Encrypt"/>.
    /// Throws <see cref="CryptographicException"/> if the key is wrong or the data
    /// was tampered with. Callers treat that exception as "wrong password / corrupt".
    /// </summary>
    public static byte[] Decrypt(byte[] key, byte[] blob)
    {
        ValidateKey(key);
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted blob is too short to be valid.");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[blob.Length - NonceSize - TagSize];

        Buffer.BlockCopy(blob, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(blob, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(blob, NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static void ValidateKey(byte[] key)
    {
        if (key is null || key.Length != Argon2KeyDeriver.KeyLengthBytes)
            throw new ArgumentException("Key must be 32 bytes (256-bit).", nameof(key));
    }
}
