using System.Text;

namespace AiLimitMonitor.Core.Rendering;

/// <summary>
/// Wraps text in a box border with a little hamster running clockwise along it, one step per
/// tick. The border is always drawn — hiding the pet must not change the layout. The pet emoji
/// displays two cells wide, so it always replaces exactly two border/padding cells: alignment
/// is preserved everywhere (the inner padding column absorbs it on the left/right edges).
/// </summary>
public static class PetBorder
{
    public const string Pet = "🐹";

    public static string Wrap(string content, long tick, bool showPet = true)
    {
        var lines = content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var width = lines.Max(l => l.Length);
        var totalWidth = width + 4;  // "│ " + content + " │"
        var totalHeight = lines.Length + 2;

        var rows = new List<string>
        {
            "┌" + new string('─', width + 2) + "┐",
        };
        foreach (var line in lines)
            rows.Add("│ " + line.PadRight(width) + " │");
        rows.Add("└" + new string('─', width + 2) + "┘");

        if (showPet)
        {
            var (x, y) = PetPosition(totalWidth, totalHeight, tick);
            // The two display cells the pet occupies: clamped on the horizontal edges,
            // pinned to the border+padding pair on the vertical edges.
            var start = y == 0 || y == totalHeight - 1
                ? Math.Min(x, totalWidth - 2)
                : (x == 0 ? 0 : totalWidth - 2);
            rows[y] = rows[y].Remove(start, 2).Insert(start, Pet);
        }

        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.AppendLine(row);
        return sb.ToString();
    }

    /// <summary>Clockwise walk over the border cells, starting at the top-left corner.</summary>
    public static (int X, int Y) PetPosition(int totalWidth, int totalHeight, long tick)
    {
        var perimeter = PerimeterLength(totalWidth, totalHeight);
        var step = (int)(((tick % perimeter) + perimeter) % perimeter);

        var top = totalWidth;                // y = 0, x = 0..totalWidth-1
        var right = totalHeight - 2;         // x = totalWidth-1, y = 1..totalHeight-2
        var bottom = totalWidth;             // y = totalHeight-1, x = totalWidth-1..0

        if (step < top)
            return (step, 0);
        step -= top;
        if (step < right)
            return (totalWidth - 1, step + 1);
        step -= right;
        if (step < bottom)
            return (totalWidth - 1 - step, totalHeight - 1);
        step -= bottom;
        return (0, totalHeight - 2 - step);
    }

    public static int PerimeterLength(int totalWidth, int totalHeight) =>
        2 * totalWidth + 2 * (totalHeight - 2);
}
