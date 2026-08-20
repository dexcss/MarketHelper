using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketHelper;

/// <summary>One resolved line from a MakePlace list.</summary>
public sealed class MakePlaceEntry
{
    public string Name = string.Empty;
    public int Quantity;
    public uint ItemId;
}

/// <summary>Outcome of parsing a MakePlace shopping list.</summary>
public sealed class MakePlaceResult
{
    public readonly List<MakePlaceEntry> Items = new();
    public readonly List<string> Unbuyable = new();
    public string? Error;
    public int LinesRead;

    public int TotalUnits => Items.Sum(i => i.Quantity);
}

/// <summary>
/// Parses the shopping list MakePlace exports.
///
/// The file has SEVERAL sections and only the first one is wanted:
///
///     Furniture            &lt;- this one
///     =====================
///     Ale Tap: 2
///     ...
///     (blank line)
///     Dyes                 &lt;- skipped
///     =====================
///     ...
///     Furniture (With Dye) &lt;- skipped: same furniture again, split by dye colour,
///     =====================    so importing it too would double every quantity
///
/// The header is matched EXACTLY (after trimming the centring spaces), which is what keeps
/// "Furniture (With Dye)" out. The section ends at the first blank line, separator, or any line
/// that isn't "Name: Quantity".
/// </summary>
public static class MakePlaceImport
{
    private const string FurnitureHeader = "Furniture";

    public static MakePlaceResult ParseFurniture(string text)
    {
        var result = new MakePlaceResult();
        if (string.IsNullOrWhiteSpace(text))
        {
            result.Error = "Nothing to import — paste a list or load a file first.";
            return result;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.Equals(lines[i].Trim(), FurnitureHeader, StringComparison.OrdinalIgnoreCase)) continue;
            start = i + 1;
            break;
        }
        if (start < 0)
        {
            result.Error = "No \"Furniture\" section found. (A \"Furniture (With Dye)\" section on its own isn't used — it repeats the same items split by dye.)";
            return result;
        }

        var started = false;
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (!started)
            {
                if (IsSeparator(line)) { started = true; continue; }
                if (line.Length == 0) continue;
                started = true;   // no separator under the header — treat as straight into items
            }

            if (line.Length == 0) break;        // blank line ends the section
            if (IsSeparator(line)) break;       // next section's rule

            var split = line.LastIndexOf(':');
            if (split <= 0) break;              // a header, not an item line

            var name = line[..split].Trim();
            var qtyText = new string(line[(split + 1)..].Where(char.IsDigit).ToArray());
            if (name.Length == 0 || qtyText.Length == 0) break;
            if (!int.TryParse(qtyText, out var qty) || qty <= 0) continue;

            result.LinesRead++;

            // Exact match only. Fuzzy matching here would be actively dangerous: "Everkeep Sofa"
            // and "Curved Everkeep Sofa" are different furnishings at different prices.
            var id = ItemSearch.FindExact(name);
            if (id != 0)
            {
                var existing = result.Items.FirstOrDefault(e => e.ItemId == id);
                if (existing != null) existing.Quantity += qty;
                else result.Items.Add(new MakePlaceEntry { Name = name, Quantity = qty, ItemId = id });
                continue;
            }

            // Known item, just not sellable on the market board (crafted-only, quest reward, etc).
            var anyId = ItemSearch.FindExactAny(name);
            result.Unbuyable.Add(anyId != 0
                ? $"UNABLE TO BUY — {name} x{qty} (not sold on the market board)"
                : $"UNABLE TO BUY — {name} x{qty} (no item by that name)");
        }

        if (result.Items.Count == 0 && result.Unbuyable.Count == 0)
            result.Error = "Found the Furniture section but no \"Name: Quantity\" lines in it.";

        return result;
    }

    private static bool IsSeparator(string trimmed)
        => trimmed.Length > 0 && trimmed.All(c => c == '=' || c == '-');
}
