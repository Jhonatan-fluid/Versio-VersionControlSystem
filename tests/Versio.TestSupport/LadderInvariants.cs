using System.Globalization;
using Versio.Core.Iec.Ladder;

namespace Versio.TestSupport;

/// <summary>
/// What every ladder drawing must satisfy, whatever its shape and whichever vendor's parser
/// produced it.
/// </summary>
/// <remarks>
/// Here rather than in one test project because two need it: Core checks the layout over
/// generated rungs, and each vendor checks its own specimens with the same rules. A copy in each
/// would drift, and a drawing that passes one copy and fails the other is a bug nobody could pin
/// down.
/// </remarks>
public static class LadderInvariants
{
    /// <summary>Every rule the drawing breaks, as readable lines. Empty when it breaks none.</summary>
    public static IReadOnlyList<string> Violations(LadderDrawing drawing)
    {
        ArgumentNullException.ThrowIfNull(drawing);

        var problems = new List<string>();
        var elements = drawing.Groups.Where(g => g.Key.Length > 0).ToList();
        var all = new LdRect(0, 0, drawing.Width, drawing.Height);

        for (var i = 0; i < elements.Count; i++)
        {
            for (var j = i + 1; j < elements.Count; j++)
            {
                if (elements[i].Bounds.Overlaps(elements[j].Bounds))
                {
                    problems.Add($"{elements[i].Key} overlaps {elements[j].Key}");
                }
            }

            foreach (var text in elements[i].Items.OfType<LdText>())
            {
                var b = text.Bounds;
                if (!elements[i].Bounds.Contains(b.X, b.Y) || !elements[i].Bounds.Contains(b.Right, b.Bottom))
                {
                    problems.Add($"'{text.Text}' runs outside {elements[i].Key}");
                }
            }
        }

        var lines = drawing.Groups.SelectMany(g => g.Items.OfType<LdLine>().Select(l => (Group: g, Line: l))).ToList();
        foreach (var (group, line) in lines)
        {
            if (!all.Contains(line.X1, line.Y1) || !all.Contains(line.X2, line.Y2))
            {
                problems.Add($"a {group.Kind} line leaves the drawing");
            }

            if (Math.Min(line.X1, line.X2) < 4 - 0.01)
            {
                problems.Add($"a {group.Kind} line is left of the rail");
            }
        }

        // Every wire end touches another line, and every element's input lead starts on one:
        // a wire that ends in empty space is a connection the diagram invented or lost.
        foreach (var (_, line) in lines.Where(l => l.Group.Kind == "wire"))
        {
            foreach (var (x, y) in new[] { (line.X1, line.Y1), (line.X2, line.Y2) })
            {
                if (!lines.Any(other => !ReferenceEquals(other.Line, line) && OnSegment(other.Line, x, y)))
                {
                    problems.Add($"a wire end at ({F(x)}, {F(y)}) touches nothing");
                }
            }
        }

        foreach (var element in elements)
        {
            var leads = element.Items.OfType<LdLine>()
                .Where(l => Math.Abs(l.X1 - element.Bounds.X) < 0.01 && Math.Abs(l.Y1 - l.Y2) < 0.01);
            foreach (var lead in leads)
            {
                if (!lines.Any(other => !ReferenceEquals(other.Group, element) && OnSegment(other.Line, lead.X1, lead.Y1)))
                {
                    problems.Add($"{element.Key}'s input at ({F(lead.X1)}, {F(lead.Y1)}) is not connected");
                }
            }
        }

        return problems;
    }

    private static string F(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static bool OnSegment(LdLine l, double x, double y)
    {
        const double e = 0.01;
        return x >= Math.Min(l.X1, l.X2) - e && x <= Math.Max(l.X1, l.X2) + e
            && y >= Math.Min(l.Y1, l.Y2) - e && y <= Math.Max(l.Y1, l.Y2) + e
            && Math.Abs(((l.X2 - l.X1) * (y - l.Y1)) - ((l.Y2 - l.Y1) * (x - l.X1))) < 0.5;
    }
}
