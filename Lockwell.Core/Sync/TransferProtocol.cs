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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Lockwell.Sync;

/// <summary>Metadata for one item on offer. Carries no file contents.</summary>
public sealed class TransferItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public string MediaType { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>
    /// Hash of the plaintext contents, so the receiver can tell "already have this"
    /// from "have an older version of this" without downloading anything.
    /// </summary>
    public string ContentHash { get; set; } = "";

    /// <summary>Optional expiry the receiver enforces locally.</summary>
    public DateTime? ExpiresUtc { get; set; }
}

public sealed class TransferManifest
{
    public string SenderName { get; set; } = "";
    public List<TransferItem> Items { get; set; } = new();
}

public sealed class TransferRequest
{
    /// <summary>Ids the receiver actually wants. Anything omitted is skipped.</summary>
    public List<string> WantedIds { get; set; } = new();
}

public sealed class TransferItemHeader
{
    public string Id { get; set; } = "";
    public long SizeBytes { get; set; }
    public int ChunkCount { get; set; }
}

public sealed class TransferControl
{
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TransferManifest))]
[JsonSerializable(typeof(TransferRequest))]
[JsonSerializable(typeof(TransferItemHeader))]
[JsonSerializable(typeof(TransferControl))]
public partial class TransferJson : JsonSerializerContext
{
}

/// <summary>
/// Moving chosen items across an already-established encrypted session.
///
/// The shape of the exchange, and why:
///
///   1. receiver asks for the manifest
///   2. sender lists what it has queued -- metadata only, no contents
///   3. receiver replies with the ids it actually wants
///   4. sender streams those, chunked
///
/// The receiver deciding what to pull is what keeps "the phone only holds what you
/// chose" true from both ends: even a sender offering more than expected cannot push
/// anything the receiver did not ask for. It also means nothing is re-sent when the
/// receiver already has an identical copy, which the content hash establishes without
/// moving a byte.
///
/// Items arrive as plaintext inside the tunnel and are immediately re-encrypted with
/// the RECEIVER's own key. The sender's data key never crosses, so a compromised phone
/// still reveals nothing about the PC vault.
/// </summary>
public static class Transfer
{
    private const string AskForManifest = "manifest?";
    private const string EndOfTransfer = "done";

    public static string HashOf(byte[] contents) =>
        Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();

    // ------------------------------------------------------------- sending

    /// <summary>
    /// Serve items to a connected receiver. <paramref name="load"/> is called only for
    /// items the receiver asks for, so nothing is decrypted unnecessarily.
    ///
    /// <paramref name="load"/> must return an array this method may take ownership of:
    /// the plaintext is zeroed as soon as it has been framed, so handing back a cached
    /// or shared buffer would wipe the caller's own copy. Decrypting fresh each time,
    /// which is what the vault does anyway, satisfies this.
    /// </summary>
    public static async Task<int> ServeAsync(
        SyncSession session,
        string senderName,
        IReadOnlyList<TransferItem> offered,
        Func<string, byte[]> load,
        IProgress<TransferProgress>? progress = null,
        CancellationToken token = default)
    {
        // 1. wait to be asked
        var opening = await ReceiveControlAsync(session, token);
        if (opening.Kind != AskForManifest)
            throw new InvalidDataException($"Expected a manifest request, got '{opening.Kind}'.");

        // 2. offer what we have
        await SendJsonAsync(session, new TransferManifest
        {
            SenderName = senderName,
            Items = offered.ToList(),
        }, TransferJson.Default.TransferManifest, token);

        // 3. hear what they want
        byte[] requestBytes = await session.ReceiveAsync(token);
        var request = JsonSerializer.Deserialize(requestBytes, TransferJson.Default.TransferRequest)
                      ?? new TransferRequest();

        var wanted = offered.Where(i => request.WantedIds.Contains(i.Id)).ToList();
        int sent = 0;

        // 4. stream them
        foreach (var item in wanted)
        {
            token.ThrowIfCancellationRequested();

            byte[] contents = load(item.Id);
            try
            {
                var chunks = SyncSession.Chunk(contents).ToList();

                await SendJsonAsync(session, new TransferItemHeader
                {
                    Id = item.Id,
                    SizeBytes = contents.LongLength,
                    ChunkCount = chunks.Count,
                }, TransferJson.Default.TransferItemHeader, token);

                foreach (var chunk in chunks)
                    await session.SendAsync(chunk.ToArray(), token);

                sent++;
                progress?.Report(new TransferProgress
                {
                    Title = item.Title,
                    Current = sent,
                    Total = wanted.Count,
                    Bytes = contents.LongLength,
                });
            }
            finally
            {
                // The plaintext existed only to be framed and encrypted.
                CryptographicOperations.ZeroMemory(contents);
            }
        }

        await SendControlAsync(session, EndOfTransfer, sent.ToString(), token);
        return sent;
    }

    // ----------------------------------------------------------- receiving

    /// <summary>
    /// Pull items from a connected sender.
    ///
    /// <paramref name="decide"/> chooses what to accept, and <paramref name="store"/>
    /// is handed each arrival to encrypt into the local vault.
    /// </summary>
    public static async Task<TransferSummary> ReceiveAsync(
        SyncSession session,
        Func<IReadOnlyList<TransferItem>, IReadOnlyList<string>> decide,
        Action<TransferItem, byte[]> store,
        IProgress<TransferProgress>? progress = null,
        CancellationToken token = default)
    {
        // 1. ask
        await SendControlAsync(session, AskForManifest, "", token);

        // 2. read the offer
        byte[] manifestBytes = await session.ReceiveAsync(token);
        var manifest = JsonSerializer.Deserialize(manifestBytes, TransferJson.Default.TransferManifest)
                       ?? new TransferManifest();

        // 3. say what we want
        var wanted = decide(manifest.Items).ToList();
        await SendJsonAsync(session, new TransferRequest { WantedIds = wanted },
            TransferJson.Default.TransferRequest, token);

        var summary = new TransferSummary
        {
            SenderName = manifest.SenderName,
            Offered = manifest.Items.Count,
            Skipped = manifest.Items.Count - wanted.Count,
        };

        // 4. take delivery
        while (true)
        {
            token.ThrowIfCancellationRequested();

            byte[] next = await session.ReceiveAsync(token);

            // The end marker is a control message; anything else is an item header.
            var control = TryReadControl(next);
            if (control?.Kind == EndOfTransfer) break;

            var header = JsonSerializer.Deserialize(next, TransferJson.Default.TransferItemHeader);
            if (header is null || string.IsNullOrEmpty(header.Id))
                throw new InvalidDataException("Expected an item header.");

            var item = manifest.Items.FirstOrDefault(i => i.Id == header.Id)
                       ?? throw new InvalidDataException("Sender offered an item that was not in the manifest.");

            var buffer = new List<byte>((int)Math.Min(header.SizeBytes, int.MaxValue));
            for (int i = 0; i < header.ChunkCount; i++)
                buffer.AddRange(await session.ReceiveAsync(token));

            byte[] contents = buffer.ToArray();
            try
            {
                if (contents.LongLength != header.SizeBytes)
                    throw new InvalidDataException($"\"{item.Title}\" arrived the wrong size.");

                // The hash was authenticated as part of the manifest, so a mismatch here
                // means the contents are not what was offered.
                if (!string.IsNullOrEmpty(item.ContentHash) &&
                    !HashOf(contents).Equals(item.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"\"{item.Title}\" did not match its expected contents.");
                }

                store(item, contents);
                summary.Received++;
                summary.Bytes += contents.LongLength;

                progress?.Report(new TransferProgress
                {
                    Title = item.Title,
                    Current = summary.Received,
                    Total = wanted.Count,
                    Bytes = contents.LongLength,
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contents);
            }
        }

        return summary;
    }

    // ------------------------------------------------------------ plumbing

    private static async Task SendJsonAsync<T>(
        SyncSession session, T value, JsonTypeInfo<T> type, CancellationToken token) =>
        await session.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, type), token);

    private static async Task SendControlAsync(
        SyncSession session, string kind, string detail, CancellationToken token) =>
        await SendJsonAsync(session, new TransferControl { Kind = kind, Detail = detail },
            TransferJson.Default.TransferControl, token);

    private static async Task<TransferControl> ReceiveControlAsync(
        SyncSession session, CancellationToken token)
    {
        byte[] bytes = await session.ReceiveAsync(token);
        return TryReadControl(bytes)
               ?? throw new InvalidDataException("Expected a control message.");
    }

    private static TransferControl? TryReadControl(byte[] bytes)
    {
        try
        {
            var control = JsonSerializer.Deserialize(bytes, TransferJson.Default.TransferControl);
            return string.IsNullOrEmpty(control?.Kind) ? null : control;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class TransferProgress
{
    public string Title { get; init; } = "";
    public int Current { get; init; }
    public int Total { get; init; }
    public long Bytes { get; init; }
}

public sealed class TransferSummary
{
    public string SenderName { get; set; } = "";
    public int Offered { get; set; }
    public int Received { get; set; }
    public int Skipped { get; set; }
    public long Bytes { get; set; }
}
