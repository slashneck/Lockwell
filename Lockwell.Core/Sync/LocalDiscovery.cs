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

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lockwell.Sync;

/// <summary>What a PC says about itself when a phone asks who is out there.</summary>
public sealed class DiscoveryBeacon
{
    public int Version { get; set; } = SyncProtocol.Version;

    /// <summary>The name the user gave this PC. Cosmetic, and chosen by them.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The short fingerprint of this PC's identity key. Safe to say out loud: it is the
    /// same string both screens already show for the user to compare, and knowing it
    /// grants nothing. The full public key is not here; it arrives over TCP, once, to a
    /// device that actually connected.
    /// </summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>TCP port to connect to. Sent rather than assumed, so the port can move.</summary>
    public int Port { get; set; } = SyncProtocol.DefaultPort;

    /// <summary>True while this PC is waiting for a new device to pair.</summary>
    public bool AcceptingPairing { get; set; }

    /// <summary>Filled in by the finder from where the reply came, not by the sender.</summary>
    [JsonIgnore]
    public string Address { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(DiscoveryBeacon))]
public partial class DiscoveryJson : JsonSerializerContext
{
}

/// <summary>
/// Finding a Lockwell PC on the local network, so nobody has to type an IP address.
///
/// Typing an address is the single worst step in linking. It asks a person to read a
/// number off one screen and copy it into another, it fails silently when the machine has
/// more than one address, and it gives no clue when the other end simply is not listening.
/// This replaces it: the phone asks the network "any Lockwell PCs there?" and shows what
/// answers.
///
/// Deliberately small, and deliberately not always on:
///
///   - The PC only answers while the user has the linking screen open. A vault app that
///     announced itself to the whole network all day would be telling everyone on that
///     network which machine is worth attacking, for no benefit.
///   - The reply carries a name, a fingerprint and a port. No vault data, no public key,
///     nothing that helps anyone who is not already being invited in.
///   - Being found means nothing on its own. A discovered PC still has to complete the
///     same authenticated handshake with the same one-time pairing code, and the user
///     still compares fingerprints. Discovery removes typing, not proof.
///
/// UDP broadcast rather than mDNS: mDNS would mean a dependency or a hand-rolled DNS
/// implementation, and this needs to answer exactly one question on one subnet.
/// </summary>
public static class LocalDiscovery
{
    /// <summary>Sits next to the sync port so the two are obviously a pair.</summary>
    public const int DiscoveryPort = SyncProtocol.DefaultPort + 1;

    /// <summary>
    /// What a probe says. A fixed string rather than an empty packet, so this never
    /// answers something that merely happened to arrive on the port.
    /// </summary>
    private const string ProbeMagic = "LOCKWELL-WHO-IS-THERE-v1";

    private const string ReplyMagic = "LOCKWELL-HERE-v1:";

    /// <summary>Nothing on this port should ever be large; anything bigger is not ours.</summary>
    private const int MaxPacket = 2048;

    // ------------------------------------------------------------- responding

    /// <summary>
    /// Answer probes until cancelled. Run by the PC while its linking screen is open.
    ///
    /// <paramref name="describe"/> is called per probe rather than once, so the reply
    /// always reflects the current state, including whether pairing is still being
    /// accepted.
    /// </summary>
    public static async Task RespondAsync(
        Func<DiscoveryBeacon> describe, CancellationToken token = default)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.EnableBroadcast = true;

        // Sharing the address means a second Lockwell window, or a stale socket, does not
        // stop this one from binding.
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // A refused ICMP from an earlier reply surfaces here on Windows. Keep going.
                continue;
            }

            if (received.Buffer.Length is 0 or > MaxPacket) continue;

            string text;
            try { text = Encoding.UTF8.GetString(received.Buffer); }
            catch { continue; }

            if (!text.StartsWith(ProbeMagic, StringComparison.Ordinal)) continue;

            byte[] reply;
            try
            {
                DiscoveryBeacon beacon = describe();
                string json = JsonSerializer.Serialize(beacon, DiscoveryJson.Default.DiscoveryBeacon);
                reply = Encoding.UTF8.GetBytes(ReplyMagic + json);
            }
            catch
            {
                continue;
            }

            try
            {
                await socket.SendAsync(reply, reply.Length, received.RemoteEndPoint);
            }
            catch
            {
                // The asker vanished between probe and reply. Nothing to do about it.
            }
        }
    }

    // --------------------------------------------------------------- finding

    /// <summary>
    /// Ask who is out there and collect the answers.
    ///
    /// Probes more than once because a single UDP packet is allowed to vanish and often
    /// does on a busy network, and because the PC may open its linking screen a moment
    /// after the phone starts looking.
    /// </summary>
    public static async Task<IReadOnlyList<DiscoveryBeacon>> FindAsync(
        TimeSpan timeout, CancellationToken token = default)
    {
        var found = new Dictionary<string, DiscoveryBeacon>(StringComparer.Ordinal);

        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.EnableBroadcast = true;
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // Port zero: the OS picks one, and replies come back to it.
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);

        byte[] probe = Encoding.UTF8.GetBytes(ProbeMagic);

        async Task SendProbesAsync()
        {
            var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, DiscoveryPort) };

            // Some networks drop 255.255.255.255 but pass a subnet-directed broadcast, so
            // send to both. Each local address implies its own subnet's broadcast.
            foreach (LocalAddress local in LocalNetwork.Detect())
            {
                if (!IPAddress.TryParse(local.Address, out IPAddress? ip)) continue;

                byte[] octets = ip.GetAddressBytes();
                octets[3] = 255;                       // assumes /24, which home networks are
                targets.Add(new IPEndPoint(new IPAddress(octets), DiscoveryPort));
            }

            while (!deadline.IsCancellationRequested)
            {
                foreach (IPEndPoint target in targets)
                {
                    try { await socket.SendAsync(probe, probe.Length, target); }
                    catch { /* one unreachable target must not stop the others */ }
                }

                try { await Task.Delay(TimeSpan.FromMilliseconds(700), deadline.Token); }
                catch (OperationCanceledException) { return; }
            }
        }

        Task probing = SendProbesAsync();

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await socket.ReceiveAsync(deadline.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }

                if (received.Buffer.Length is 0 or > MaxPacket) continue;

                string text;
                try { text = Encoding.UTF8.GetString(received.Buffer); }
                catch { continue; }

                if (!text.StartsWith(ReplyMagic, StringComparison.Ordinal)) continue;

                DiscoveryBeacon? beacon;
                try
                {
                    beacon = JsonSerializer.Deserialize(
                        text[ReplyMagic.Length..], DiscoveryJson.Default.DiscoveryBeacon);
                }
                catch
                {
                    continue;   // malformed, or not really one of ours
                }

                if (beacon is null) continue;
                if (beacon.Version != SyncProtocol.Version) continue;

                // The address is taken from where the packet came from, never from the
                // packet itself, so a beacon cannot point a phone somewhere else.
                beacon.Address = received.RemoteEndPoint.Address.ToString();

                // Keyed by fingerprint: one PC answering on two adapters is one PC, and
                // the first reply is the one whose address arrived fastest.
                string key = beacon.Fingerprint.Length > 0 ? beacon.Fingerprint : beacon.Address;
                found.TryAdd(key, beacon);
            }
        }
        finally
        {
            deadline.Cancel();
            try { await probing; } catch { /* shutting down */ }
        }

        return found.Values
            .OrderByDescending(b => b.AcceptingPairing)
            .ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
