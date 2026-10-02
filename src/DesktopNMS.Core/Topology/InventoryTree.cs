using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Topology;

/// <summary>One entry in a device's physical inventory tree and what it contains.</summary>
public sealed class InventoryNode
{
    public InventoryNode(InventoryEntry entry, int depth)
    {
        Entry = entry;
        Depth = depth;
    }

    public InventoryEntry Entry { get; }

    /// <summary>0 for a root (usually the chassis).</summary>
    public int Depth { get; }

    public List<InventoryNode> Children { get; } = new();

    /// <summary>This node and everything under it, depth first.</summary>
    public IEnumerable<InventoryNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }
}

/// <summary>
/// Builds a device's physical inventory tree (#164) from LibreNMS's flat
/// <c>entPhysical</c> rows: each row's <see cref="InventoryEntry.ContainedIn"/>
/// names its parent's <see cref="InventoryEntry.Index"/>, 0 meaning a root.
/// Siblings are ordered by their position in the parent, then index - the
/// order the device itself reports.
/// </summary>
public static class InventoryTree
{
    public static IReadOnlyList<InventoryNode> Build(IEnumerable<InventoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // One entry per index - a duplicate index (a device reporting the
        // same entity twice) would otherwise make a node its own sibling.
        var byIndex = new Dictionary<long, InventoryEntry>();
        foreach (var entry in entries)
        {
            byIndex.TryAdd(entry.Index, entry);
        }

        var children = byIndex.Values
            .Where(e => e.ContainedIn != 0 && e.ContainedIn != e.Index && byIndex.ContainsKey(e.ContainedIn))
            .GroupBy(e => e.ContainedIn)
            .ToDictionary(g => g.Key, g => Order(g).ToList());

        // A root is anything not inside another listed entry - including one
        // whose parent LibreNMS didn't return, so nothing is ever lost.
        var roots = Order(byIndex.Values.Where(e => e.ContainedIn == 0 || e.ContainedIn == e.Index || !byIndex.ContainsKey(e.ContainedIn)));

        var placed = new HashSet<long>();
        var result = new List<InventoryNode>();

        foreach (var root in roots)
        {
            result.Add(Place(root, 0));
        }

        // Anything still unplaced sits in a containment loop (A in B, B in
        // A) - a device bug, but shown at the top rather than dropped.
        foreach (var leftover in Order(byIndex.Values.Where(e => !placed.Contains(e.Index))))
        {
            if (!placed.Contains(leftover.Index))
            {
                result.Add(Place(leftover, 0));
            }
        }

        return result;

        InventoryNode Place(InventoryEntry entry, int depth)
        {
            placed.Add(entry.Index);
            var node = new InventoryNode(entry, depth);

            if (children.TryGetValue(entry.Index, out var kids))
            {
                foreach (var kid in kids.Where(k => !placed.Contains(k.Index)))
                {
                    node.Children.Add(Place(kid, depth + 1));
                }
            }

            return node;
        }
    }

    private static IEnumerable<InventoryEntry> Order(IEnumerable<InventoryEntry> entries) =>
        entries
            .OrderBy(e => e.ParentRelPos < 0 ? long.MaxValue : e.ParentRelPos)
            .ThenBy(e => e.Index);
}
