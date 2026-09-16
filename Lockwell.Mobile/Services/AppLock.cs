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

namespace Lockwell.Mobile.Services;

/// <summary>
/// Locking the vault when the app stops being in front of you.
///
/// A vault that stays unlocked while the app sits in the background is unlocked whenever
/// the phone is handed to someone, picked up off a table, or taken. Desktop already treats
/// this as a real risk and locks on minimise; the phone had nothing.
///
/// The reason it is not simply "lock the instant you switch away" is Argon2id. Deriving
/// the key deliberately costs 256 MiB and several seconds, which is exactly what makes a
/// stolen vault expensive to attack, and exactly what would make the app miserable if
/// glancing at a notification meant paying that cost again. Biometric unlock avoids the
/// derivation entirely, so anyone who has it on can afford a short grace period; anyone
/// who has not still gets one by default rather than a punishment.
///
/// So: a grace period, adjustable, with "immediately" and "never" both available. The
/// clock starts when the app goes to the background and is checked when it comes back,
/// which means it is enforced by the phone's own wall clock and works whether the app was
/// away for a second or a week.
/// </summary>
public static class AppLock
{
    private static DateTime? _leftAt;

    /// <summary>Raised when the app returns after longer than the grace period.</summary>
    public static event Action? ShouldLock;

    /// <summary>
    /// How long the app may sit in the background before the vault locks.
    /// Zero locks immediately; null never locks on backgrounding.
    /// </summary>
    public static TimeSpan? Grace { get; set; } = TimeSpan.FromSeconds(60);

    public static void WentToBackground() => _leftAt = DateTime.UtcNow;

    /// <summary>
    /// Called when the app comes back. Locks if it was away too long.
    ///
    /// Uses the wall clock rather than a timer, because a timer in a suspended process is
    /// not running: a phone put down for an hour would otherwise come back unlocked.
    /// </summary>
    public static void CameToForeground()
    {
        DateTime? left = _leftAt;
        _leftAt = null;

        if (left is null) return;
        if (Grace is not { } grace) return;              // never lock on backgrounding

        if (DateTime.UtcNow - left.Value >= grace) ShouldLock?.Invoke();
    }

    /// <summary>Forget any pending grace period, after the vault has been locked anyway.</summary>
    public static void Reset() => _leftAt = null;

    /// <summary>Wording for the current setting, so the UI and the docs agree.</summary>
    public static string Describe() => Grace switch
    {
        null => "Never lock when leaving the app",
        { TotalSeconds: <= 0 } => "Lock immediately when leaving the app",
        { TotalSeconds: < 60 } g => $"Lock after {(int)g.TotalSeconds} seconds away",
        { TotalMinutes: < 60 } g => $"Lock after {(int)g.TotalMinutes} minute(s) away",
        var g => $"Lock after {(int)g.Value.TotalHours} hour(s) away",
    };
}
