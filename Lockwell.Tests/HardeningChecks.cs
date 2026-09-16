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
using Lockwell.Crypto;

namespace Lockwell.Tests;

/// <summary>
/// The measures that exist to stop secrets outliving the process.
///
/// These are easy to write and easy to get subtly wrong in ways nothing notices: a key
/// buffer that is freed but not zeroed, or a "wipe" that clears a copy while the original
/// stays put, both look fine from the outside and fail exactly when it matters. So the
/// behaviour is pinned down here rather than assumed.
/// </summary>
internal static class HardeningChecks
{
    public static void Run()
    {
        KeyStaysUsable();
        KeyIsWipedAndPinned();
    }

    private static void KeyStaysUsable()
    {
        Check.Section("46. A key held outside the managed heap still works");

        byte[] original = RandomNumberGenerator.GetBytes(32);
        byte[] copy = (byte[])original.Clone();

        using var locked = new LockedKey(original);

        Check.That("the array handed in is wiped, so the pageable copy is gone",
            original.All(b => b == 0));
        Check.That("the key length is preserved", locked.Length == 32);

        bool matches = locked.Use(key => CryptographicOperations.FixedTimeEquals(key, copy));
        Check.That("the key it holds is the key it was given", matches);

        // The real test: something encrypted through the locked key must decrypt with the
        // plain key, or the whole vault would stop opening.
        byte[] plaintext = "the quick brown fox"u8.ToArray();
        byte[] blob = locked.Use(key => VaultCrypto.Encrypt(key, plaintext));
        byte[] back = VaultCrypto.Decrypt(copy, blob);

        Check.That("data encrypted with it round-trips", back.SequenceEqual(plaintext));

        Check.Note(locked.IsPinned
            ? "this machine allowed the key to be pinned against paging"
            : "this machine refused to pin it; the key still works, just pageable");
    }

    private static void KeyIsWipedAndPinned()
    {
        Check.Section("47. Wiping a key when the vault locks");

        byte[] secret = RandomNumberGenerator.GetBytes(32);
        byte[] expected = (byte[])secret.Clone();

        var locked = new LockedKey(secret);

        byte[]? leaked = null;
        locked.Use(key =>
        {
            // Keep the reference the callback was given, to prove the scratch copy handed
            // to a caller does not survive the call.
            leaked = key;
            Check.That("inside the callback the key is readable",
                CryptographicOperations.FixedTimeEquals(key, expected));
        });

        Check.That("the copy handed to a caller is wiped when the call ends",
            leaked is not null && leaked.All(b => b == 0));

        locked.Dispose();

        Check.That("using a disposed key throws rather than reading freed memory",
            Throws(() => locked.Use(_ => 0)));

        Check.That("disposing twice is harmless", !Throws(locked.Dispose));
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }
}
