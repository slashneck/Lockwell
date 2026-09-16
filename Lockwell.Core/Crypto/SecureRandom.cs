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
/// Single source of cryptographically secure randomness for the whole app.
/// Everything that needs a salt, nonce, key, or recovery code comes through here
/// so there is exactly one place to audit for "is this random actually secure".
/// </summary>
public static class SecureRandom
{
    /// <summary>Return <paramref name="length"/> bytes from the OS CSPRNG.</summary>
    public static byte[] GetBytes(int length)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
    }
}
