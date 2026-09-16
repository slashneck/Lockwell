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

namespace Lockwell.Sync;

/// <summary>
/// The last step of pairing: one person, on one device, says the two codes match.
///
/// It used to take two confirmations, one on each screen, which was both more work than
/// necessary and slightly dishonest about what was being confirmed. The device that was
/// connected *to* has nothing to verify by itself; it cannot see the other screen. Only
/// the person holding both devices can compare, and they only need to say so once.
///
/// So the connecting device asks, and the answer travels back through the tunnel the
/// handshake just established. That tunnel is already authenticated by the pairing code
/// and the key exchange, so nothing on the network can forge the answer or flip a "no"
/// into a "yes". A refusal is sent too, rather than simply hanging up, so the other end
/// can say "that was rejected" instead of "something went wrong".
/// </summary>
public static class PairingConfirmation
{
    private const string Accepted = "pair-confirm:yes";
    private const string Rejected = "pair-confirm:no";

    /// <summary>
    /// Tell the other device whether the codes matched. Sent by the device the person is
    /// holding and looking at, which is the one that connected.
    /// </summary>
    public static async Task SendAsync(SyncSession session, bool matched, string deviceName,
        CancellationToken token = default)
    {
        string body = (matched ? Accepted : Rejected) + "\n" + deviceName;
        await session.SendAsync(Encoding.UTF8.GetBytes(body), token);
    }

    /// <summary>
    /// Wait for the answer. Returns whether it was accepted, and the name the other device
    /// calls itself, so the receiving side can label it without asking again.
    /// </summary>
    public static async Task<(bool Accepted, string DeviceName)> ReceiveAsync(
        SyncSession session, CancellationToken token = default)
    {
        byte[] payload = await session.ReceiveAsync(token);
        string body = Encoding.UTF8.GetString(payload);

        int split = body.IndexOf('\n');
        string verdict = split < 0 ? body : body[..split];
        string name = split < 0 ? "" : body[(split + 1)..];

        // Anything that is not an explicit yes counts as no. A garbled or unexpected
        // message must never be read as consent.
        return (verdict == Accepted, name.Trim());
    }
}
