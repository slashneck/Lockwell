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
using System.Windows.Threading;

namespace Lockwell.Services;

/// <summary>
/// Locks the vault automatically after a period of user inactivity. Idle time is
/// measured system-wide (keyboard + mouse), so leaving the PC unattended re-locks
/// the vault even if Lockwell is just sitting in the background.
/// </summary>
public sealed class AutoLockService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly DispatcherTimer _timer;
    private TimeSpan _idleLimit;

    /// <summary>Raised on the UI thread when the vault should lock.</summary>
    public event Action? LockRequested;

    public AutoLockService(TimeSpan idleLimit)
    {
        _idleLimit = idleLimit;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => CheckIdle();
    }

    public void SetIdleLimit(TimeSpan limit) => _idleLimit = limit;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void CheckIdle()
    {
        if (GetIdleTime() >= _idleLimit)
        {
            _timer.Stop();
            LockRequested?.Invoke();
        }
    }

    private static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint idleMs = (uint)Environment.TickCount - info.dwTime;
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
