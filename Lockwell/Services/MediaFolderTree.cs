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

using Lockwell.Models;

namespace Lockwell.Services;

/// <summary>
/// Folder-tree rules for the media section, kept out of the UI so they can be tested
/// directly. The important one is <see cref="CanReparent"/>: dropping a folder into
/// its own subtree would detach that whole branch from the root and the files inside
/// would stop being reachable, so it must never be allowed.
/// </summary>
public static class MediaFolderTree
{
    /// <summary>Depth cap so damaged data can never spin this into an infinite loop.</summary>
    private const int MaxDepth = 512;

    /// <summary>
    /// True when <paramref name="folderId"/> is <paramref name="possibleAncestorId"/>
    /// itself or sits somewhere beneath it.
    /// </summary>
    public static bool IsDescendantOf(
        IReadOnlyList<MediaFolder> folders, string folderId, string possibleAncestorId)
    {
        var current = folders.FirstOrDefault(f => f.Id == folderId);
        int guard = 0;
        while (current is not null && guard++ < MaxDepth)
        {
            if (current.Id == possibleAncestorId) return true;
            if (string.IsNullOrEmpty(current.ParentFolderId)) return false;
            current = folders.FirstOrDefault(f => f.Id == current.ParentFolderId);
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="folderId"/> may be moved under
    /// <paramref name="targetParentId"/> (null meaning the gallery root).
    /// </summary>
    public static bool CanReparent(
        IReadOnlyList<MediaFolder> folders, string folderId, string? targetParentId)
    {
        var folder = folders.FirstOrDefault(f => f.Id == folderId);
        if (folder is null) return false;

        // Already there: nothing to do.
        if (string.Equals(folder.ParentFolderId ?? "", targetParentId ?? "", StringComparison.Ordinal))
            return false;

        if (targetParentId is null) return true; // moving out to the root is always safe
        if (targetParentId == folderId) return false; // cannot contain itself
        if (folders.All(f => f.Id != targetParentId)) return false; // unknown target

        return !IsDescendantOf(folders, targetParentId, folderId);
    }
}

/// <summary>
/// Index maths for drag-to-reorder. Separated from the UI so the tricky part -- that
/// removing the dragged item shifts every index after it -- can be tested directly.
/// </summary>
public static class MediaOrdering
{
    /// <summary>
    /// The new arrangement after dragging <paramref name="movedId"/> to sit just before
    /// or just after <paramref name="targetId"/>. Returns the ids in their new order.
    /// Unknown ids, or a drop onto itself, leave the order untouched.
    /// </summary>
    public static List<string> Reorder(
        IReadOnlyList<string> ids, string movedId, string targetId, bool insertAfter)
    {
        var original = ids.ToList();
        if (movedId == targetId) return original;

        int from = original.IndexOf(movedId);
        if (from < 0 || !original.Contains(targetId)) return original;

        var list = new List<string>(original);
        list.RemoveAt(from);

        // Look the target up *after* the removal: its index shifts down by one when
        // the dragged item used to sit before it.
        int target = list.IndexOf(targetId);
        if (target < 0) return original;

        int insertAt = Math.Clamp(insertAfter ? target + 1 : target, 0, list.Count);
        list.Insert(insertAt, movedId);
        return list;
    }
}
