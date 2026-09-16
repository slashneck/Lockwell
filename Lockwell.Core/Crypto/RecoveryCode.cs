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

using System.Text;

namespace Lockwell.Crypto;

/// <summary>
/// Encodes/decodes the optional recovery key as human-typable text.
///
/// The recovery key is 32 bytes of full-entropy randomness. Because it is already
/// maximum-entropy, it is used directly as a key-encryption-key (no Argon2 needed).
/// We render it in Crockford Base32 (no ambiguous I/L/O/U characters) grouped into
/// blocks so a person can write it on paper and type it back reliably.
/// </summary>
public static class RecoveryCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int KeyBytes = 32;

    public static byte[] Generate() => SecureRandom.GetBytes(KeyBytes);

    public static string Format(byte[] key)
    {
        string raw = Base32Encode(key);
        var sb = new StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse a recovery key the user typed back in. Tolerates spaces, dashes,
    /// lowercase, and the common look-alike mistakes (I/L to 1, O to 0).
    /// Returns null if the text is not a valid 32-byte key.
    /// </summary>
    public static byte[]? TryParse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var cleaned = new StringBuilder();
        foreach (char ch in input.Trim().ToUpperInvariant())
        {
            char c = ch switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                'U' => 'V',
                _ => ch
            };
            if (Alphabet.IndexOf(c) >= 0) cleaned.Append(c);
        }

        try
        {
            byte[] bytes = Base32Decode(cleaned.ToString());
            return bytes.Length >= KeyBytes ? bytes[..KeyBytes] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bitsLeft = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                int index = (buffer >> (bitsLeft - 5)) & 31;
                bitsLeft -= 5;
                sb.Append(Alphabet[index]);
            }
        }
        if (bitsLeft > 0)
        {
            int index = (buffer << (5 - bitsLeft)) & 31;
            sb.Append(Alphabet[index]);
        }
        return sb.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        int buffer = 0, bitsLeft = 0;
        var output = new List<byte>(input.Length * 5 / 8 + 1);
        foreach (char c in input)
        {
            int val = Alphabet.IndexOf(c);
            if (val < 0) continue;
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return output.ToArray();
    }
}
