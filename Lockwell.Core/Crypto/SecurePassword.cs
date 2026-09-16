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

using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Lockwell.Crypto;

/// <summary>
/// Bridges the WPF password fields to the bytes the KDF needs, without the password
/// ever existing as a managed string.
///
/// Why this exists: a .NET string is immutable and moved around by the GC, so once a
/// password lands in one it can never be wiped. Copies can linger in the heap, a crash
/// dump, or the page file until the process dies. A SecureString keeps the characters
/// in unmanaged memory instead; we decrypt, copy, and zero them inside a single call,
/// so the plaintext password exists for microseconds in memory we control.
///
/// This is not magic: while the password is briefly decoded, malware already running
/// as your user could still read it. It closes the "left lying around in the heap"
/// window, not the "your machine is compromised" one.
/// </summary>
public static class SecurePassword
{
    /// <summary>
    /// UTF-8 bytes of the password. The caller owns the array and must clear it when done.
    /// </summary>
    public static byte[] ToUtf8Bytes(SecureString password)
    {
        IntPtr bstr = Marshal.SecureStringToBSTR(password);
        char[] chars = new char[password.Length];
        try
        {
            Marshal.Copy(bstr, chars, 0, chars.Length);
            byte[] bytes = new byte[Encoding.UTF8.GetByteCount(chars)];
            Encoding.UTF8.GetBytes(chars, 0, chars.Length, bytes, 0);
            return bytes;
        }
        finally
        {
            Array.Clear(chars, 0, chars.Length);
            Marshal.ZeroFreeBSTR(bstr);
        }
    }

    /// <summary>
    /// Run <paramref name="reader"/> over the decoded characters, then zero them.
    /// Used for things like strength scoring that need to look at the password
    /// without copying it into a string.
    /// </summary>
    public static T Read<T>(SecureString password, Func<char[], T> reader)
    {
        IntPtr bstr = Marshal.SecureStringToBSTR(password);
        char[] chars = new char[password.Length];
        try
        {
            Marshal.Copy(bstr, chars, 0, chars.Length);
            return reader(chars);
        }
        finally
        {
            Array.Clear(chars, 0, chars.Length);
            Marshal.ZeroFreeBSTR(bstr);
        }
    }

    /// <summary>Compare two passwords in constant time, without materialising either as a string.</summary>
    public static bool SecureEquals(SecureString a, SecureString b)
    {
        byte[] x = ToUtf8Bytes(a);
        byte[] y = ToUtf8Bytes(b);
        try
        {
            return CryptographicOperations.FixedTimeEquals(x, y);
        }
        finally
        {
            Array.Clear(x, 0, x.Length);
            Array.Clear(y, 0, y.Length);
        }
    }
}
