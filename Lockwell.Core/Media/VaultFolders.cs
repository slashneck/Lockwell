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

namespace Lockwell.Media;

/// <summary>
/// A folder, seen through only the parts the shared logic needs. Both apps keep their
/// own richer folder type; this is the slice the tree operations work on.
/// </summary>
public interface IFolderNode
{
    string Id { get; }
    string? ParentFolderId { get; set; }
    string Name { get; set; }
    int Order { get; set; }
    DateTime CreatedUtc { get; }
}

/// <summary>Whatever a folder can contain, seen through the fields sorting needs.</summary>
public interface ISortableItem
{
    string Title { get; }
    string FileName { get; }
    string MediaType { get; }
    long SizeBytes { get; }
    DateTime AddedUtc { get; }
    string? FolderId { get; set; }

    /// <summary>
    /// Position when the user has arranged things by hand. Meaningless under every other
    /// sort, which is why arranging by dragging switches the sort to
    /// <see cref="ItemSort.Custom"/> rather than silently having no effect.
    /// </summary>
    int Order { get; set; }
}

/// <summary>How a list of folders and items is ordered.</summary>
public enum ItemSort
{
    /// <summary>When it arrived. The default, because recency is what people look for.</summary>
    Added = 0,
    Name = 1,
    Size = 2,

    /// <summary>Images, then video, then audio, then everything else.</summary>
    Kind = 3,

    /// <summary>Whatever order the user dragged things into.</summary>
    Custom = 4,
}

/// <summary>
/// Folder hierarchy operations, shared so the phone and the PC agree on what a folder
/// tree means rather than each deciding separately.
///
/// The rule worth stating plainly: a folder can never end up inside itself. Allowing it
/// would orphan everything under it and hang any code that walks parents, so the move
/// is refused rather than repaired afterwards.
/// </summary>
public static class FolderTree
{
    /// <summary>
    /// Hard stop on how deep a walk will go. Data damaged into a loop should make an
    /// operation refuse, not spin forever.
    /// </summary>
    public const int MaxDepth = 512;

    /// <summary>True when <paramref name="folderId"/> is the ancestor itself, or sits under it.</summary>
    public static bool IsDescendantOf<T>(IReadOnlyList<T> folders, string? folderId, string ancestorId)
        where T : IFolderNode
    {
        if (folderId is null) return false;

        string? current = folderId;
        int depth = 0;

        while (current is not null)
        {
            // Only a walk that never ends is a damaged tree. Fail closed there, so the
            // caller refuses a move it cannot reason about. Reaching the top level is
            // the ordinary way to finish and means the two are simply unrelated.
            if (depth++ >= MaxDepth) return true;

            if (current == ancestorId) return true;

            T? node = Find(folders, current);
            if (node is null) return false;
            current = node.ParentFolderId;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="folderId"/> may be moved under <paramref name="newParentId"/>.
    /// Refuses a move into itself or into any of its own descendants.
    /// </summary>
    public static bool CanMove<T>(IReadOnlyList<T> folders, string folderId, string? newParentId)
        where T : IFolderNode
    {
        if (newParentId is null) return true;           // to the top level is always fine
        if (newParentId == folderId) return false;      // into itself
        if (Find(folders, newParentId) is null) return false;

        return !IsDescendantOf(folders, newParentId, folderId);
    }

    /// <summary>Direct children of a folder, or of the top level when null.</summary>
    public static IReadOnlyList<T> ChildrenOf<T>(IReadOnlyList<T> folders, string? parentId)
        where T : IFolderNode =>
        folders.Where(f => f.ParentFolderId == parentId).ToList();

    /// <summary>
    /// The chain from the top level down to this folder, for a breadcrumb. Empty when the
    /// folder is unknown; stops rather than loops if the data is damaged.
    /// </summary>
    public static IReadOnlyList<T> PathTo<T>(IReadOnlyList<T> folders, string? folderId)
        where T : IFolderNode
    {
        var path = new List<T>();
        if (folderId is null) return path;

        string? current = folderId;
        for (int depth = 0; depth < MaxDepth && current is not null; depth++)
        {
            T? node = Find(folders, current);
            if (node is null) break;

            path.Insert(0, node);
            current = node.ParentFolderId;
        }

        return path;
    }

    /// <summary>
    /// Every folder at or beneath this one, deepest last. Used when a folder is removed,
    /// so nothing underneath it is left pointing at a parent that no longer exists.
    /// </summary>
    public static IReadOnlyList<T> DescendantsOf<T>(IReadOnlyList<T> folders, string folderId)
        where T : IFolderNode
    {
        var found = new List<T>();
        var queue = new Queue<string>();
        queue.Enqueue(folderId);

        int guard = 0;
        while (queue.Count > 0 && guard++ < MaxDepth * 4)
        {
            string parent = queue.Dequeue();
            foreach (T child in folders.Where(f => f.ParentFolderId == parent))
            {
                found.Add(child);
                queue.Enqueue(child.Id);
            }
        }

        return found;
    }

    /// <summary>
    /// A name that does not already exist beside it. Adding "(2)" is friendlier than
    /// refusing, and keeps two folders from looking identical in a list.
    /// </summary>
    public static string UniqueName<T>(IReadOnlyList<T> folders, string? parentId, string wanted,
        string? ignoreId = null) where T : IFolderNode
    {
        string baseName = wanted.Trim();
        if (baseName.Length == 0) baseName = "Untitled";

        bool Taken(string candidate) => folders.Any(f =>
            f.ParentFolderId == parentId &&
            f.Id != ignoreId &&
            string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (!Taken(baseName)) return baseName;

        for (int n = 2; n < 1000; n++)
        {
            string candidate = $"{baseName} ({n})";
            if (!Taken(candidate)) return candidate;
        }

        return baseName + " " + Guid.NewGuid().ToString("N")[..4];
    }

    /// <summary>
    /// Move everything directly inside a folder up to where that folder was, so the
    /// folder can be removed without taking its contents with it.
    ///
    /// This is the safe half of deleting a folder, and it is separate from any deletion on
    /// purpose. Removing a container is a tidying action; tidying must never be the thing
    /// that destroys the only copy of a photo. A caller that really does want the contents
    /// gone has to say so separately, having been told how many there are.
    ///
    /// Returns how many things were moved.
    /// </summary>
    public static int LiftContentsOf<TFolder, TItem>(
        IReadOnlyList<TFolder> folders, IEnumerable<TItem> items, string folderId, string? liftTo)
        where TFolder : IFolderNode
        where TItem : ISortableItem
    {
        int moved = 0;

        foreach (TFolder child in folders)
        {
            if (child.ParentFolderId != folderId) continue;
            child.ParentFolderId = liftTo;
            moved++;
        }

        foreach (TItem item in items)
        {
            if (item.FolderId != folderId) continue;
            item.FolderId = liftTo;
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Put <paramref name="moved"/> immediately before <paramref name="target"/> in a
    /// hand-arranged list, and renumber so the positions stay dense and predictable.
    ///
    /// Dropping an item onto the very thing it already sits before is a no-op rather than
    /// an error: a small imprecise drag should never rearrange anything by surprise.
    /// Returns true when the order actually changed.
    /// </summary>
    public static bool Arrange<T>(IList<T> ordered, T moved, T? target) where T : ISortableItem
    {
        int from = ordered.IndexOf(moved);
        if (from < 0) return false;

        if (target is not null)
        {
            int at = ordered.IndexOf(target);
            if (at < 0) return false;                 // dropped on something not here
            if (at == from) return false;             // dropped on itself
            if (at == from + 1) return false;         // already immediately before it
        }

        ordered.RemoveAt(from);

        // The insertion point is worked out after the removal, not before it. Computing
        // it first and then adjusting is what made "drop past the last tile" land one
        // place short of the end.
        int to = target is null ? ordered.Count : ordered.IndexOf(target);
        if (to < 0)
        {
            ordered.Insert(from, moved);              // put it back, change nothing
            return false;
        }

        ordered.Insert(to, moved);

        for (int i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        return true;
    }

    private static T? Find<T>(IReadOnlyList<T> folders, string id) where T : IFolderNode
    {
        foreach (T folder in folders)
        {
            if (folder.Id == id) return folder;
        }
        return default;
    }
}

/// <summary>
/// Ordering for folders and items. Kept beside the tree logic so both apps sort the same
/// way, and so the harness can test the real comparer rather than a copy of it.
/// </summary>
public static class ItemOrdering
{
    /// <summary>Folders always sit above items, so this orders within the folder group.</summary>
    public static IReadOnlyList<T> SortFolders<T>(IEnumerable<T> folders, ItemSort by, bool descending)
        where T : IFolderNode
    {
        IOrderedEnumerable<T> ordered = by switch
        {
            // Size and kind mean nothing for a folder, so both fall back to the name,
            // which at least keeps the order stable and predictable.
            ItemSort.Name or ItemSort.Size or ItemSort.Kind =>
                folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => folders.OrderBy(f => f.CreatedUtc),
        };

        // Tiebreak before reversing, so equal keys keep a stable order either way.
        IEnumerable<T> result = ordered.ThenBy(f => f.Id, StringComparer.Ordinal);
        if (descending) result = result.Reverse();
        return result.ToList();
    }

    public static IReadOnlyList<T> SortItems<T>(IEnumerable<T> items, ItemSort by, bool descending)
        where T : ISortableItem
    {
        IOrderedEnumerable<T> ordered = by switch
        {
            ItemSort.Name => items.OrderBy(i => DisplayName(i), StringComparer.CurrentCultureIgnoreCase),
            ItemSort.Size => items.OrderBy(i => i.SizeBytes),
            ItemSort.Kind => items.OrderBy(i => KindRank(i))
                                  .ThenBy(i => DisplayName(i), StringComparer.CurrentCultureIgnoreCase),
            ItemSort.Custom => items.OrderBy(i => i.Order),
            _ => items.OrderBy(i => i.AddedUtc),
        };

        // Id is not part of this interface, so the last tiebreak is the name: two items
        // with an equal sort key keep a stable order between refreshes either way.
        IEnumerable<T> result = ordered.ThenBy(i => DisplayName(i), StringComparer.Ordinal);

        // A hand-arranged order is already exactly what the user wanted; reversing it
        // would just undo their arranging.
        if (descending && by != ItemSort.Custom) result = result.Reverse();
        return result.ToList();
    }

    /// <summary>What to sort a name by: the title if it has one, otherwise the file name.</summary>
    private static string DisplayName<T>(T item) where T : ISortableItem =>
        string.IsNullOrWhiteSpace(item.Title) ? item.FileName : item.Title;

    private static int KindRank<T>(T item) where T : ISortableItem
    {
        string type = item.MediaType;
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return 0;
        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return 1;
        if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    /// <summary>The label a UI shows for a sort mode, so both apps word it the same.</summary>
    public static string Label(ItemSort sort) => sort switch
    {
        ItemSort.Name => "Name",
        ItemSort.Size => "Size",
        ItemSort.Kind => "Type",
        ItemSort.Custom => "My order",
        _ => "Date added",
    };
}
