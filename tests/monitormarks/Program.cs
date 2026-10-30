// Which monitor may be acted on, and how the marks are stored. The case that matters most is the one that broke on
// real hardware (2026-10-30): the monitor connected to the Mac was swapped for another unit of the SAME MODEL, whose
// EDID serial differs, so the saved mark matched nothing — Windows refused every "switch back to this PC" silently.
using BHDisplay.Core;

int pass = 0, fail = 0;
void Ok(bool cond, string what)
{
    if (cond) { pass++; Console.WriteLine("ok   " + what); }
    else { fail++; Console.WriteLine("FAIL " + what); }
}

const string Home = "sn:V1A2B3C4D5";      // the monitor at home
const string Office = "sn:Z9Y8X7W6V5";     // same model, another unit, at the office
const string PcOnly = "sn:P0P0P0P0P0";     // the PC's own second monitor, never shared

// ---- section 0: self-check, so a broken rig cannot look like working code ----
Ok(MonitorMarks.Choose(new[] { Home }, new[] { Home }, out var only0) == MarkChoice.Chosen && only0 == Home,
   "section 0 self-check: the one marked monitor, present, is chosen");

// ---- which monitor may be acted on ----
Ok(MonitorMarks.Choose(new[] { Home, PcOnly }, new[] { Home }, out var c1) == MarkChoice.Chosen && c1 == Home,
   "the marked monitor is chosen even when an unmarked one is also attached");

Ok(MonitorMarks.Choose(new[] { Office, PcOnly }, new[] { Home }, out var c2) == MarkChoice.Missing && c2 is null,
   "THE BUG: the marked monitor was swapped for another unit → Missing, never the unmarked one");

Ok(MonitorMarks.Choose(new[] { Office, PcOnly }, new[] { Home, Office }, out var c3) == MarkChoice.Chosen && c3 == Office,
   "with both units marked, the one that is here is chosen — no re-ticking after a swap");

Ok(MonitorMarks.Choose(new[] { Home, Office }, new[] { Home, Office }, out var c4) == MarkChoice.Ambiguous && c4 is null,
   "both marked monitors attached at once → Ambiguous, never a guess");

Ok(MonitorMarks.Choose(new[] { PcOnly }, new[] { Home, Office }, out _) == MarkChoice.Missing,
   "only an unmarked monitor is attached → Missing (its screen is never taken over)");

Ok(MonitorMarks.Choose(Array.Empty<string>(), new[] { Home }, out _) == MarkChoice.Missing,
   "nothing answers DDC → Missing");

Ok(MonitorMarks.Choose(new[] { Home, PcOnly }, Array.Empty<string>(), out var c5) == MarkChoice.Unmarked && c5 is null,
   "nothing marked → Unmarked, the caller asks the user");

// A mark must not match a monitor by accident: comparison is exact and case-sensitive.
Ok(MonitorMarks.Choose(new[] { "sn:v1a2b3c4d5" }, new[] { Home }, out _) == MarkChoice.Missing,
   "marks match exactly (case-sensitive), so two serials are never confused");

// ---- how the marks are stored ----
Ok(MonitorMarks.Normalise(new List<string>(), Home) is { Count: 1 } mig && mig[0] == Home,
   "an older build's single mark is carried into the list (nothing to set up again)");

Ok(MonitorMarks.Normalise(new List<string> { Office }, Home) is { Count: 1 } keep && keep[0] == Office,
   "once the list is in use, the legacy single mark is not re-added");

Ok(MonitorMarks.Normalise(new List<string> { Home, Home, Office }, null) is { Count: 2 },
   "duplicates are dropped");

Ok(MonitorMarks.Normalise(new List<string> { "dev:\\\\.\\DISPLAY1", "sn:", "", "   ", Home }, null) is { Count: 1 } clean
   && clean[0] == Home,
   "a monitor with no EDID serial is never marked (its display slot moves when cables move)");

Ok(MonitorMarks.Normalise(Enumerable.Range(0, 50).Select(i => "sn:MON" + i).ToList(), null).Count == MonitorMarks.Max,
   "a hand-edited file cannot grow the list without bound");

Ok(MonitorMarks.Normalise(new List<string> { "sn:" + new string('x', 200) }, null).Count == 0,
   "an absurdly long key is refused");

Ok(MonitorMarks.Normalise(null, null).Count == 0 && MonitorMarks.Normalise(null, "").Count == 0,
   "no marks and no legacy value is simply empty");

Ok(!MonitorMarks.IsKey(null) && !MonitorMarks.IsKey("sn:") && MonitorMarks.IsKey(Home),
   "IsKey accepts only a serial-based mark");

// ---- the states that must make Windows DECLINE a switch request instead of accepting it ----
// Accepting cancels the Mac's 2 s fallback, so "accepted then refused" strands the monitor on the Mac for good.
// Windows declines when it cannot act: no marked monitor here, nothing answering at all, or two marked ones here.
static bool MustDecline(string[] answering, string[] marked) =>
    marked.Length > 0 && MonitorMarks.Choose(answering, marked, out _) != MarkChoice.Chosen;

Ok(MustDecline(new[] { Office, PcOnly }, new[] { Home }),
   "declines when the marked monitor was swapped for another unit");
Ok(MustDecline(Array.Empty<string>(), new[] { Home }),
   "declines when nothing answers DDC at all (the flag must not be left stale)");
Ok(MustDecline(new[] { Home, Office }, new[] { Home, Office }),
   "declines when two marked monitors are plugged in (ambiguous, not just missing)");
Ok(!MustDecline(new[] { Office, PcOnly }, new[] { Home, Office }),
   "accepts when exactly one marked monitor is here");
Ok(!MustDecline(new[] { PcOnly }, Array.Empty<string>()),
   "nothing marked is not a decline: that is the single-monitor case the user never set up");

// ---- non-vacuity: the OLD single-mark rule fails the swap case these tests now cover ----
static MarkChoice OldRule(string[] answering, string marked)
{   // what 1.3.7 did: one saved serial, acted on only if exactly one attached monitor matched it
    if (marked.Length == 0) return MarkChoice.Unmarked;
    var hits = answering.Count(k => k == marked);
    return hits == 1 ? MarkChoice.Chosen : MarkChoice.Missing;
}
Ok(OldRule(new[] { Office, PcOnly }, Home) == MarkChoice.Missing
   && MonitorMarks.Choose(new[] { Office, PcOnly }, new[] { Home, Office }, out _) == MarkChoice.Chosen,
   "non-vacuous: the old rule is stuck on a swapped monitor where the new one succeeds");

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
