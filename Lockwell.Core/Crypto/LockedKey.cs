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
using System.Security.Cryptography;

namespace Lockwell.Crypto;

/// <summary>
/// A key held in memory the operating system has been asked never to write to disk.
///
/// The problem this solves is quiet and easy to miss. A `byte[]` holding the vault's data
/// key lives on the managed heap, and the managed heap is ordinary pageable memory. If
/// Windows runs short, it can write those pages to `pagefile.sys`. If the machine
/// hibernates, they land in `hiberfil.sys`. Either way a copy of the key that unlocks
/// everything ends up on the disk, outliving the process, outside anything the vault
/// controls. Zeroing the array on lock does not help: the copy already on disk is not the
/// array.
///
/// So the key is kept outside the managed heap instead:
///
///   - allocated with unmanaged memory, so the garbage collector never moves it and never
///     leaves an unreachable copy at an old address,
///   - pinned in physical memory with VirtualLock, which asks Windows not to page it out,
///   - zeroed and unlocked when the vault locks.
///
/// Two honest limits, because overstating this would be worse than not doing it:
///
///   - VirtualLock is a request against paging, not against memory being read. Anything
///     running as this user, or any debugger attached to the process, can still read it.
///     That is the same limit the threat model already states for an unlocked vault.
///   - It does not stop a full-memory crash dump from containing the key. Locked pages are
///     still part of the process.
///
/// What it does close is the case where the key survives on disk after the process is
/// gone, which is the one an attacker gets for free later.
/// </summary>
public sealed class LockedKey : IDisposable
{
    private IntPtr _buffer;
    private readonly int _length;
    private bool _locked;
    private bool _disposed;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualLock(IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualUnlock(IntPtr address, UIntPtr size);

    /// <summary>
    /// Take ownership of <paramref name="key"/>, copying it somewhere unpageable and
    /// wiping the array that was handed in. The caller must not keep using it afterwards.
    /// </summary>
    public LockedKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        _length = key.Length;
        _buffer = Marshal.AllocHGlobal(_length);

        try
        {
            Marshal.Copy(key, 0, _buffer, _length);

            // A failure here is not fatal: the key still works, it is simply pageable.
            // Refusing to open the vault over it would be a worse trade for the user.
            _locked = VirtualLock(_buffer, (UIntPtr)(uint)_length);
        }
        finally
        {
            // The managed copy has served its purpose and is the pageable one.
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Whether the operating system agreed to keep this out of the page file.</summary>
    public bool IsPinned => _locked;

    public int Length => _length;

    /// <summary>
    /// Run <paramref name="work"/> against a copy of the key.
    ///
    /// A copy, because every consumer of a key in .NET wants a `byte[]`, and handing one
    /// out permanently would put the key straight back on the pageable heap. This keeps
    /// the exposure to the span of one operation, and wipes it afterwards even if that
    /// operation throws.
    /// </summary>
    public T Use<T>(Func<byte[], T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] scratch = new byte[_length];
        try
        {
            Marshal.Copy(_buffer, scratch, 0, _length);
            return work(scratch);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scratch);
        }
    }

    public void Use(Action<byte[]> work) => Use<object?>(key => { work(key); return null; });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_buffer == IntPtr.Zero) return;

        // Zero before unlocking, so the cleared pages are the ones that were pinned.
        for (int i = 0; i < _length; i++) Marshal.WriteByte(_buffer, i, 0);

        if (_locked)
        {
            VirtualUnlock(_buffer, (UIntPtr)(uint)_length);
            _locked = false;
        }

        Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }
}
