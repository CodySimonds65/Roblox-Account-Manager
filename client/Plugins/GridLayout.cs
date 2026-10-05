namespace RobloxAltClient.Plugins;

internal readonly record struct LayoutRect(int Left, int Top, int Width, int Height)
{
    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);
}

internal static class GridLayout
{
    public const int MinimumCellWidth = 160;
    public const int MinimumCellHeight = 120;

    // Returns one cell per window, monitor by monitor in reading order. Work
    // areas share the windows in proportion to their size so a large monitor
    // never ends up with tiny cells while a small one sits half empty.
    public static IReadOnlyList<LayoutRect> Compute(IReadOnlyList<LayoutRect> workAreas, int count)
    {
        if (count <= 0) return [];
        var areas = workAreas.Where(area => area.Width > 0 && area.Height > 0)
            .OrderBy(area => area.Left).ThenBy(area => area.Top).ToArray();
        if (areas.Length == 0) areas = [new LayoutRect(0, 0, 1920, 1080)];

        var cells = new List<LayoutRect>(count);
        var shares = Distribute(areas, count);
        for (var index = 0; index < areas.Length; index++) cells.AddRange(Tile(areas[index], shares[index]));
        return cells;
    }

    internal static int[] Distribute(IReadOnlyList<LayoutRect> areas, int count)
    {
        var shares = new int[areas.Count];
        if (areas.Count == 0 || count <= 0) return shares;
        var total = areas.Sum(area => (double)area.Area);
        var remainders = new double[areas.Count];
        var assigned = 0;
        for (var index = 0; index < areas.Count; index++)
        {
            var exact = count * areas[index].Area / total;
            shares[index] = (int)Math.Floor(exact);
            remainders[index] = exact - shares[index];
            assigned += shares[index];
        }
        // Largest remainder first; ties go to the larger, then earlier, area so
        // the result is deterministic.
        foreach (var index in Enumerable.Range(0, areas.Count)
                     .OrderByDescending(index => remainders[index])
                     .ThenByDescending(index => areas[index].Area)
                     .ThenBy(index => index)
                     .Take(count - assigned))
        {
            shares[index]++;
        }
        return shares;
    }

    internal static IReadOnlyList<LayoutRect> Tile(LayoutRect work, int count)
    {
        if (count <= 0) return [];
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling(count / (double)columns);
        var width = Math.Max(1, work.Width / columns);
        var height = Math.Max(1, work.Height / rows);
        var cells = new LayoutRect[count];
        for (var index = 0; index < count; index++)
        {
            var x = work.Left + (index % columns) * width;
            var y = work.Top + (index / columns) * height;
            cells[index] = new LayoutRect(x, y, Math.Max(MinimumCellWidth, width), Math.Max(MinimumCellHeight, height));
        }
        return cells;
    }
}
