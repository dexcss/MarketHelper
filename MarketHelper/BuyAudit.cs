using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ECommons.DalamudServices;

namespace MarketHelper;

public enum AuditKind
{
    RunStart, Stop, Arrive, Item, Purchase, DryBuy, Skip, Refused, Pause, Resume, Finish, Problem,
}

/// <summary>One line of the audit trail for a Buyer run.</summary>
public sealed class BuyAuditEntry
{
    public DateTime Time = DateTime.Now;
    public AuditKind Kind;
    public string World = string.Empty;
    public string DataCenter = string.Empty;
    public string Item = string.Empty;
    public int Quantity;
    public long UnitPrice;
    public long Expected;     // what we expected to pay in total for this purchase
    public long Actual;       // what actually left the wallet (0 when not a purchase)
    public string Detail = string.Empty;

    /// <summary>
    /// A purchase whose real cost doesn't match what was planned. Market tax adds up to ~5%, so
    /// anything inside 10% plus rounding is normal; beyond that is worth your attention, and that
    /// is exactly what an audit log is for.
    /// </summary>
    public bool Flagged =>
        Kind == AuditKind.Purchase && Expected > 0 && Actual > (long)(Expected * 1.10) + 100;

    public long Difference => Actual - Expected;
}

/// <summary>
/// Writes a run's audit trail to the plugin config folder as CSV, so a run can be checked over
/// after the fact — or handed to someone else — rather than scrolled back through in chat.
/// </summary>
public static class BuyAuditWriter
{
    public static string FolderPath()
    {
        var baseDir = Svc.PluginInterface.GetPluginConfigDirectory();
        return Path.Combine(baseDir, "buyer-logs");
    }

    /// <summary>Writes the log and returns the full path, or null if it couldn't be written.</summary>
    public static string? Save(IReadOnlyList<BuyAuditEntry> entries, bool dryRun)
    {
        if (entries.Count == 0) return null;
        try
        {
            var dir = FolderPath();
            Directory.CreateDirectory(dir);

            var name = $"buy-{DateTime.Now:yyyyMMdd-HHmmss}{(dryRun ? "-dryrun" : "")}.csv";
            var path = Path.Combine(dir, name);

            var sb = new StringBuilder();
            sb.AppendLine("Time,Kind,World,DataCenter,Item,Quantity,UnitPrice,ExpectedTotal,ActualCost,Difference,Flagged,Detail");
            foreach (var e in entries)
            {
                sb.Append(Csv(e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',');
                sb.Append(Csv(e.Kind.ToString())).Append(',');
                sb.Append(Csv(e.World)).Append(',');
                sb.Append(Csv(e.DataCenter)).Append(',');
                sb.Append(Csv(e.Item)).Append(',');
                sb.Append(e.Quantity.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(e.UnitPrice.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(e.Expected.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(e.Actual.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append((e.Kind == AuditKind.Purchase ? e.Difference : 0).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(e.Flagged ? "YES" : "").Append(',');
                sb.AppendLine(Csv(e.Detail));
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Prune(dir);
            return path;
        }
        catch { return null; }
    }

    /// <summary>Keep the last 40 logs so this can't grow without bound.</summary>
    private static void Prune(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("buy-*.csv")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(40)
                .ToList();
            foreach (var f in files) f.Delete();
        }
        catch { /* housekeeping only */ }
    }

    private static string Csv(string? value)
    {
        var v = value ?? string.Empty;
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
        return '"' + v.Replace("\"", "\"\"") + '"';
    }
}
