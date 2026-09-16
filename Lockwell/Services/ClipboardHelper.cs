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
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Lockwell.Services;

/// <summary>
/// Copies a secret to the clipboard and wipes it again after a short delay. Only a
/// hash of the copied value is kept in memory, never the secret itself.
/// </summary>
public static class ClipboardHelper
{
    private static DispatcherTimer? _clearTimer;
    private static byte[]? _lastCopiedHash;

    public static void CopySecret(string value, int clearAfterSeconds = 30)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        try
        {
            Clipboard.SetText(value);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(hash);
            return;
        }

        if (_lastCopiedHash is not null)
            CryptographicOperations.ZeroMemory(_lastCopiedHash);
        _lastCopiedHash = hash;

        _clearTimer?.Stop();
        _clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(clearAfterSeconds) };
        _clearTimer.Tick += (_, _) =>
        {
            _clearTimer?.Stop();
            try
            {
                if (Clipboard.ContainsText() && _lastCopiedHash is not null)
                {
                    byte[] currentHash = SHA256.HashData(Encoding.UTF8.GetBytes(Clipboard.GetText()));
                    bool same = currentHash.Length == _lastCopiedHash.Length &&
                                CryptographicOperations.FixedTimeEquals(currentHash, _lastCopiedHash);
                    CryptographicOperations.ZeroMemory(currentHash);
                    if (same)
                        Clipboard.Clear();
                }
            }
            catch { /* ignore */ }

            if (_lastCopiedHash is not null)
            {
                CryptographicOperations.ZeroMemory(_lastCopiedHash);
                _lastCopiedHash = null;
            }
        };
        _clearTimer.Start();
    }
}
