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
/// Turns decrypted image bytes into something the UI can show, without ever putting the
/// picture on disk.
///
/// This is the security-relevant half of viewing media in the app. Handing a decrypted
/// photo to another app to display means writing it to a file first, and a file is
/// something the media scanner can index, a backup can capture and a file manager can
/// list. The whole point of the vault is that the picture does not exist outside it.
///
/// So the bytes go from the encrypted attachment, through decryption in memory, into a
/// bitmap in memory, and to the screen. Nothing touches storage at any point. Writing a
/// real file happens only when the user explicitly asks for a copy, which is a separate
/// and clearly worded action.
///
/// Downscaling matters for more than speed: a grid of full-resolution phone photos will
/// exhaust memory long before it fills the screen, and decoding at the size actually
/// needed is what keeps a large vault usable.
/// </summary>
public interface IImageDecoder
{
    /// <summary>
    /// Decode <paramref name="source"/> down to roughly <paramref name="maxEdge"/> pixels
    /// on its longest side, returning encoded bytes ready for an ImageSource. Returns null
    /// if the bytes are not an image this device can decode.
    /// </summary>
    byte[]? Thumbnail(byte[] source, int maxEdge);

    /// <summary>
    /// Decode at full size for the viewer, still in memory, but capped so that a very
    /// large photo cannot exhaust memory on the way to the screen. Applies the same
    /// orientation correction as <see cref="Thumbnail"/>.
    /// </summary>
    byte[]? ForViewing(byte[] source, int maxEdge);

    /// <summary>Pixel size of an image without decoding the whole thing, for layout.</summary>
    (int Width, int Height) Measure(byte[] source);

    /// <summary>
    /// A still frame from a video, so the gallery shows the video rather than a generic
    /// icon standing in for it.
    ///
    /// Same rule as everything else here: the video is never written out to be read back.
    /// The frame is pulled from the bytes in memory. Returns null when the device cannot
    /// decode the format, and the tile falls back to its icon.
    /// </summary>
    byte[]? VideoPoster(byte[] source, int maxEdge);
}
