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

using Lockwell.Media;

namespace Lockwell.Tests;

/// <summary>
/// Folders and sorting, as the phone's media library uses them.
///
/// These run against the real <see cref="FolderTree"/> and <see cref="ItemOrdering"/>
/// that both apps call, not a copy of the rules written for the test. That distinction
/// matters: a re-implementation passes happily while the code it describes drifts away
/// underneath it.
///
/// The rule with teeth is the one about cycles. A folder moved inside itself orphans
/// everything under it and hangs any walk up the parent chain, so the move has to be
/// refused before it happens rather than repaired afterwards.
/// </summary>
internal static class FolderChecks
{
    private sealed class Folder : IFolderNode
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string? ParentFolderId { get; set; }
        public string Name { get; set; } = "";
        public int Order { get; set; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    }

    private sealed class Item : ISortableItem
    {
        public string Title { get; init; } = "";
        public string FileName { get; init; } = "";
        public string MediaType { get; init; } = "";
        public long SizeBytes { get; init; }
        public DateTime AddedUtc { get; init; } = DateTime.UtcNow;
        public string? FolderId { get; set; }
        public int Order { get; set; }
    }

    public static void Run()
    {
        Tree();
        Cycles();
        Naming();
        Sorting();
        RemovingAFolder();
        Arranging();
    }

    private static void Tree()
    {
        Check.Section("40. Folder hierarchy");

        var trip = new Folder { Id = "trip", Name = "Trip" };
        var day1 = new Folder { Id = "day1", Name = "Day one", ParentFolderId = "trip" };
        var beach = new Folder { Id = "beach", Name = "Beach", ParentFolderId = "day1" };
        var other = new Folder { Id = "other", Name = "Receipts" };
        var all = new List<Folder> { trip, day1, beach, other };

        Check.That("the top level holds only folders with no parent",
            FolderTree.ChildrenOf(all, null).Count == 2);
        Check.That("a folder lists its direct children only",
            FolderTree.ChildrenOf(all, "trip").Count == 1);

        var path = FolderTree.PathTo(all, "beach");
        Check.That("a breadcrumb runs from the top down", path.Count == 3);
        Check.That("and starts at the outermost folder", path[0].Id == "trip");
        Check.That("and ends at the folder itself", path[2].Id == "beach");

        Check.That("the top level has no breadcrumb",
            FolderTree.PathTo(all, null).Count == 0);
        Check.That("an unknown folder yields no breadcrumb rather than throwing",
            FolderTree.PathTo(all, "nonexistent").Count == 0);

        var under = FolderTree.DescendantsOf(all, "trip");
        Check.That("everything beneath a folder is found, not just its children",
            under.Count == 2 && under.Any(f => f.Id == "beach"));
        Check.That("an unrelated folder is not swept in",
            under.All(f => f.Id != "other"));
        Check.That("a leaf folder has no descendants",
            FolderTree.DescendantsOf(all, "beach").Count == 0);
    }

    private static void Cycles()
    {
        Check.Section("41. A folder can never end up inside itself");

        var a = new Folder { Id = "a", Name = "A" };
        var b = new Folder { Id = "b", Name = "B", ParentFolderId = "a" };
        var c = new Folder { Id = "c", Name = "C", ParentFolderId = "b" };
        var loose = new Folder { Id = "loose", Name = "Loose" };
        var all = new List<Folder> { a, b, c, loose };

        Check.That("a folder is a descendant of itself",
            FolderTree.IsDescendantOf(all, "a", "a"));
        Check.That("a grandchild is a descendant of its grandparent",
            FolderTree.IsDescendantOf(all, "c", "a"));
        Check.That("an unrelated folder is not",
            !FolderTree.IsDescendantOf(all, "loose", "a"));

        Check.That("moving a folder into itself is refused",
            !FolderTree.CanMove(all, "a", "a"));
        Check.That("moving a folder into its own child is refused",
            !FolderTree.CanMove(all, "a", "b"));
        Check.That("moving a folder into its own grandchild is refused",
            !FolderTree.CanMove(all, "a", "c"));

        Check.That("moving a folder somewhere unrelated is allowed",
            FolderTree.CanMove(all, "loose", "c"));
        Check.That("moving a folder to the top level is always allowed",
            FolderTree.CanMove(all, "c", null));
        Check.That("moving into a folder that does not exist is refused",
            !FolderTree.CanMove(all, "loose", "ghost"));

        // Data damaged into a loop must make the walk stop and refuse, not spin.
        var x = new Folder { Id = "x", Name = "X", ParentFolderId = "y" };
        var y = new Folder { Id = "y", Name = "Y", ParentFolderId = "x" };
        var damaged = new List<Folder> { x, y };
        Check.That("a corrupted parent loop terminates instead of hanging",
            FolderTree.IsDescendantOf(damaged, "x", "unrelated"));
        Check.That("and any move inside it is refused",
            !FolderTree.CanMove(damaged, "x", "y"));
    }

    private static void Naming()
    {
        Check.Section("42. Folder names beside each other");

        var all = new List<Folder>
        {
            new() { Id = "1", Name = "Photos" },
            new() { Id = "2", Name = "Photos (2)" },
            new() { Id = "3", Name = "Photos", ParentFolderId = "1" },
        };

        Check.That("a free name is used as typed",
            FolderTree.UniqueName(all, null, "Receipts") == "Receipts");
        Check.That("a clash gets the next free number",
            FolderTree.UniqueName(all, null, "Photos") == "Photos (3)");
        Check.That("the same name inside a different folder is not a clash",
            FolderTree.UniqueName(all, "2", "Photos") == "Photos");
        Check.That("renaming a folder to its own name is not a clash with itself",
            FolderTree.UniqueName(all, null, "Photos", ignoreId: "1") == "Photos");
        Check.That("an empty name becomes something rather than nothing",
            FolderTree.UniqueName(all, null, "   ") == "Untitled");
        Check.That("case is not a way to sneak a duplicate past",
            FolderTree.UniqueName(all, null, "photos") != "photos");
    }

    /// <summary>
    /// Removing a folder must not remove what is in it. Tidying up a container is not a
    /// reason to destroy the only copy of a photo, and this project's standing rule is
    /// that nothing is deleted as a side effect of something else.
    /// </summary>
    private static void RemovingAFolder()
    {
        Check.Section("44. Deleting a folder keeps what was inside it");

        var outer = new Folder { Id = "outer", Name = "Trip" };
        var inner = new Folder { Id = "inner", Name = "Day one", ParentFolderId = "outer" };
        var deeper = new Folder { Id = "deeper", Name = "Beach", ParentFolderId = "inner" };
        var folders = new List<Folder> { outer, inner, deeper };

        var kept = new Item { Title = "Sunset", FolderId = "inner" };
        var elsewhere = new Item { Title = "Receipt", FolderId = "outer" };
        var loose = new Item { Title = "Note", FolderId = null };
        var items = new List<Item> { kept, elsewhere, loose };

        int moved = FolderTree.LiftContentsOf(folders, items, "inner", liftTo: "outer");

        Check.That("both the child folder and the item were moved", moved == 2);
        Check.That("an item inside the removed folder survives", items.Contains(kept));
        Check.That("and now sits where that folder was", kept.FolderId == "outer");
        Check.That("a folder inside it survives too", folders.Contains(deeper));
        Check.That("and is reparented rather than orphaned", deeper.ParentFolderId == "outer");

        Check.That("something in a different folder is untouched", elsewhere.FolderId == "outer");
        Check.That("something at the top level is untouched", loose.FolderId is null);
        Check.That("nothing at all was deleted", items.Count == 3 && folders.Count == 3);

        // Removing a top-level folder lifts its contents to the top level.
        var solo = new Folder { Id = "solo", Name = "Solo" };
        var insideSolo = new Item { Title = "Photo", FolderId = "solo" };
        FolderTree.LiftContentsOf(new List<Folder> { solo }, new List<Item> { insideSolo },
            "solo", liftTo: null);
        Check.That("contents of a top-level folder rise to the top level",
            insideSolo.FolderId is null);

        int none = FolderTree.LiftContentsOf(folders, items, "empty-folder", liftTo: null);
        Check.That("lifting an empty folder moves nothing and throws nothing", none == 0);
    }

    /// <summary>
    /// Dragging things into a chosen order. The rule that keeps this from being annoying
    /// is that a drag which changes nothing must do nothing: a slightly imprecise finger
    /// should not silently rearrange a library.
    /// </summary>
    private static void Arranging()
    {
        Check.Section("45. Arranging items by hand");

        Item A() => new() { Title = "A", FileName = "a", MediaType = "image/png", Order = 0 };

        var a = new Item { Title = "A", MediaType = "image/png", Order = 0 };
        var b = new Item { Title = "B", MediaType = "image/png", Order = 1 };
        var c = new Item { Title = "C", MediaType = "image/png", Order = 2 };
        var list = new List<Item> { a, b, c };

        Check.That("moving the last item before the first reports a change",
            FolderTree.Arrange(list, c, a));
        Check.That("and it lands there", list[0].Title == "C");
        Check.That("with the others shifted along",
            list[1].Title == "A" && list[2].Title == "B");
        Check.That("positions are renumbered densely from zero",
            list[0].Order == 0 && list[1].Order == 1 && list[2].Order == 2);

        Check.That("dropping an item onto itself changes nothing",
            !FolderTree.Arrange(list, list[1], list[1]));
        Check.That("dropping an item onto something not in the list changes nothing",
            !FolderTree.Arrange(list, list[0], A()));
        Check.That("moving an item that is not in the list changes nothing",
            !FolderTree.Arrange(list, A(), list[0]));

        // A null target means "put it at the end", which is what dropping past the last
        // tile should do.
        var order = new List<Item> { a, b, c };
        for (int i = 0; i < order.Count; i++) order[i].Order = i;
        Check.That("a null target sends the item to the end",
            FolderTree.Arrange(order, order[0], null));
        Check.That("and it really is last", order[^1].Title == a.Title);

        // Custom order must survive a sort, and must not be reversed by the direction
        // toggle, which would undo the arranging.
        var arranged = new List<Item>
        {
            new() { Title = "third", MediaType = "image/png", Order = 2 },
            new() { Title = "first", MediaType = "image/png", Order = 0 },
            new() { Title = "second", MediaType = "image/png", Order = 1 },
        };

        var sorted = ItemOrdering.SortItems(arranged, ItemSort.Custom, descending: false);
        Check.That("sorting by my order follows the positions",
            sorted[0].Title == "first" && sorted[2].Title == "third");

        var reversed = ItemOrdering.SortItems(arranged, ItemSort.Custom, descending: true);
        Check.That("and reversing does not undo a hand-arranged order",
            reversed[0].Title == "first");

        Check.That("the sort mode has a label of its own",
            ItemOrdering.Label(ItemSort.Custom) == "My order");
    }

    private static void Sorting()
    {
        Check.Section("43. Sorting a folder's contents");

        var now = DateTime.UtcNow;
        var items = new List<Item>
        {
            new() { Title = "Beach", MediaType = "image/jpeg", SizeBytes = 300, AddedUtc = now.AddDays(-1) },
            new() { Title = "Anthem", MediaType = "audio/mpeg", SizeBytes = 100, AddedUtc = now.AddDays(-3) },
            new() { Title = "Clip", MediaType = "video/mp4", SizeBytes = 900, AddedUtc = now.AddDays(-2) },
            new() { Title = "Notes", MediaType = "application/pdf", SizeBytes = 50, AddedUtc = now },
        };

        var byName = ItemOrdering.SortItems(items, ItemSort.Name, descending: false);
        Check.That("by name runs A to Z", byName[0].Title == "Anthem" && byName[3].Title == "Notes");

        var byNameDesc = ItemOrdering.SortItems(items, ItemSort.Name, descending: true);
        Check.That("and reverses cleanly", byNameDesc[0].Title == "Notes");

        var bySize = ItemOrdering.SortItems(items, ItemSort.Size, descending: true);
        Check.That("by size puts the largest first", bySize[0].Title == "Clip");
        Check.That("and the smallest last", bySize[3].Title == "Notes");

        var byDate = ItemOrdering.SortItems(items, ItemSort.Added, descending: true);
        Check.That("by date added puts the newest first", byDate[0].Title == "Notes");

        var byKind = ItemOrdering.SortItems(items, ItemSort.Kind, descending: false);
        Check.That("by type groups images first", byKind[0].MediaType.StartsWith("image/"));
        Check.That("then video", byKind[1].MediaType.StartsWith("video/"));
        Check.That("then audio", byKind[2].MediaType.StartsWith("audio/"));
        Check.That("then everything else", byKind[3].MediaType == "application/pdf");

        // An item with no title sorts by its file name rather than by an empty string.
        var untitled = new List<Item>
        {
            new() { Title = "", FileName = "aardvark.png", MediaType = "image/png" },
            new() { Title = "Zebra", FileName = "z.png", MediaType = "image/png" },
        };
        var mixed = ItemOrdering.SortItems(untitled, ItemSort.Name, descending: false);
        Check.That("an untitled item sorts by its file name",
            mixed[0].FileName == "aardvark.png");

        Check.That("sorting an empty list is fine",
            ItemOrdering.SortItems(new List<Item>(), ItemSort.Name, false).Count == 0);

        var folders = new List<Folder>
        {
            new() { Id = "1", Name = "Zoo", CreatedUtc = now.AddDays(-5) },
            new() { Id = "2", Name = "Apples", CreatedUtc = now },
        };
        var sortedFolders = ItemOrdering.SortFolders(folders, ItemSort.Name, descending: false);
        Check.That("folders sort by name too", sortedFolders[0].Name == "Apples");

        var byMade = ItemOrdering.SortFolders(folders, ItemSort.Added, descending: true);
        Check.That("and by when they were made", byMade[0].Name == "Apples");

        // Size is meaningless for a folder, so it must not throw or reorder randomly.
        var bySizeFolders = ItemOrdering.SortFolders(folders, ItemSort.Size, descending: false);
        Check.That("sorting folders by size falls back to name rather than failing",
            bySizeFolders[0].Name == "Apples");

        Check.That("every sort mode has a label for the UI",
            ItemOrdering.Label(ItemSort.Added).Length > 0 &&
            ItemOrdering.Label(ItemSort.Kind) == "Type");
    }
}
