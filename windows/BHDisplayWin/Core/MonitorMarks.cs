// Which monitor BHDisplay is allowed to act on, and how the user's marks are stored.
//
// The user marks every monitor that is also connected to the Mac — one mark per PHYSICAL monitor, kept by its EDID
// serial. A list rather than a single choice, because the same model at two desks (one at the office, one at home)
// is a different unit with a different serial: marked once each, both are then recognised wherever they are plugged
// in. No platform code here, so the rules are covered by tests that run anywhere (tests/monitormarks).
using System;
using System.Collections.Generic;
using System.Linq;

namespace BHDisplay.Core;

/// What a lookup decided about the monitors that answered DDC.
public enum MarkChoice
{
    /// Nothing is marked: the caller decides (use the only monitor, or ask when there are several).
    Unmarked,
    /// Exactly one marked monitor is here — act on that one.
    Chosen,
    /// Monitors are marked but none of them is here: off, unplugged, or swapped for another unit. Refuse and say so.
    Missing,
    /// Several marked monitors are here at once: refuse rather than pick the wrong one.
    Ambiguous,
}

public static class MonitorMarks
{
    /// How many monitors may be marked (one per physical monitor the user moves between).
    public const int Max = 8;

    /// Decides which of the monitors that answered DDC may be acted on.
    /// Never falls back to an unmarked monitor: a PC paired with a Mac but working on another screen must not have
    /// that screen switched or turned off underneath it.
    public static MarkChoice Choose(IReadOnlyList<string> answeringKeys, IReadOnlyList<string> marked, out string? chosen)
    {
        chosen = null;
        if (answeringKeys == null) throw new ArgumentNullException(nameof(answeringKeys));
        if (marked == null || marked.Count == 0) return MarkChoice.Unmarked;
        var here = answeringKeys.Where(k => k != null && marked.Contains(k, StringComparer.Ordinal)).ToList();
        if (here.Count == 0) return MarkChoice.Missing;
        if (here.Count > 1) return MarkChoice.Ambiguous;
        chosen = here[0];
        return MarkChoice.Chosen;
    }

    /// The marks to keep: a single mark from an older build is carried over, and a hand-edited file cannot grow
    /// without bound or hold anything but well-formed EDID keys.
    public static List<string> Normalise(IEnumerable<string>? keys, string? legacySingle)
    {
        var all = new List<string>();
        if (keys != null) all.AddRange(keys);
        // An older build marked exactly one monitor: carry it over so nothing has to be set up again.
        if (all.Count == 0 && IsKey(legacySingle)) all.Add(legacySingle!);
        return all.Where(IsKey).Distinct(StringComparer.Ordinal).Take(Max).ToList();
    }

    /// A mark is a MonitorEntry.Key built from an EDID serial ("sn:<serial>"); a monitor that reports no serial is
    /// identified only by its Windows display slot, which moves when cables move, so it is never marked.
    public static bool IsKey(string? key) =>
        key != null && key.Length > 3 && key.Length <= 80 && key.StartsWith("sn:", StringComparison.Ordinal);
}
