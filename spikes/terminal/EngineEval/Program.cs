using System.Diagnostics;
using System.Text;
using EngineEval;

if (args.Contains("--debug-reflow")) { DebugReflow(); return; }
// P0 terminal spike: run identical VT conformance cases + a throughput benchmark against each engine.
const int C = 80, R = 24;
Func<IEngine>[] factories =
[
    () => new XTermNetEngine(C, R),
    () => new TerminalEmulationEngine(C, R),
    () => new VirtualTerminalEngine(C, R),
];

static byte[] B(string s) => Encoding.UTF8.GetBytes(s.Replace("\\e", "\u001b"));
const string ESC = "\u001b";

var cases = new List<(string Name, string Category, Func<IEngine, string?> Run)>
{
    // returns null on pass, otherwise a description of what was observed
    ("autowrap at col 80", "basic", e => { e.Feed(B(new string('x', 85))); return Expect(e.Row(0) == new string('x', 80) && e.Row(1) == "xxxxx" && e.Cursor == (1, 5), $"r0={e.Row(0).Length} r1='{e.Row(1)}' cur={e.Cursor}"); }),
    ("tab stops", "basic", e => { e.Feed(B("a\tb")); return Expect(e.Cursor == (0, 9) && e.Row(0) == "a       b", $"'{e.Row(0)}' cur={e.Cursor}"); }),
    ("CJK width 2", "unicode", e => { e.Feed(B("中文AB")); return Expect(e.Cursor == (0, 6) && e.Row(0) == "中文AB" && e.CellWidth(0, 0) == 2 && e.CellWidth(0, 1) == 0, $"'{e.Row(0)}' cur={e.Cursor} w0={e.CellWidth(0,0)} w1={e.CellWidth(0,1)}"); }),
    ("CJK wraps whole glyph at last col", "unicode", e => { e.Feed(B(ESC + "[1;80H中")); return Expect(e.Row(1).StartsWith("中") && e.Cursor == (1, 2), $"r0end='{e.Row(0)}' r1='{e.Row(1)}' cur={e.Cursor}"); }),
    ("Japanese kana + fullwidth", "unicode", e => { e.Feed(B("ｶﾅかなＡ")); return Expect(e.Cursor == (0, 8), $"cur={e.Cursor} '{e.Row(0)}'"); }),
    ("combining mark (e + U+0301)", "unicode", e => { e.Feed(B("éx")); return Expect(e.Cursor == (0, 2) && e.Row(0).Normalize() == "éx".Normalize(), $"cur={e.Cursor} '{e.Row(0)}'"); }),
    ("ZWJ emoji family = 2 cols", "unicode", e => { e.Feed(B("👨‍👩‍👧X")); return Expect(e.Cursor == (0, 3) && e.Row(0) == "👨‍👩‍👧X", $"cur={e.Cursor} '{e.Row(0)}'"); }),
    ("UTF-8 split across feeds", "unicode", e => { foreach (var b in B("中文")) e.Feed(new[] { b }); return Expect(e.Row(0) == "中文" && e.Cursor == (0, 4), $"'{e.Row(0)}' cur={e.Cursor}"); }),
    ("scroll region DECSTBM", "screen", e => { e.Feed(B("L1\r\nL2\r\nL3\r\nL4\r\nL5" + ESC + "[2;4r" + ESC + "[4;1H\nNEW")); return Expect(e.Row(0) == "L1" && e.Row(1) == "L3" && e.Row(2) == "L4" && e.Row(3) == "NEW" && e.Row(4) == "L5", $"[{e.Row(0)}|{e.Row(1)}|{e.Row(2)}|{e.Row(3)}|{e.Row(4)}]"); }),
    ("alt screen 1049 save/restore", "screen", e => { e.Feed(B("main" + ESC + "[?1049h" + ESC + "[Halt")); var inAlt = e.Row(0); e.Feed(B(ESC + "[?1049l")); return Expect(inAlt == "alt" && e.Row(0) == "main" && e.Cursor == (0, 4), $"alt='{inAlt}' after='{e.Row(0)}' cur={e.Cursor}"); }),
    ("ICH/DCH/ECH", "screen", e => { e.Feed(B("abcdef" + ESC + "[1;2H" + ESC + "[2@" + ESC + "[1;6H" + ESC + "[1P" + ESC + "[1;1H" + ESC + "[1X")); return Expect(e.Row(0) == "   bcef", $"'{e.Row(0)}'"); }),
    ("IL/DL", "screen", e => { e.Feed(B("A\r\nB\r\nC" + ESC + "[2;1H" + ESC + "[1L")); var afterIl = $"{e.Row(0)}{e.Row(1)}{e.Row(2)}{e.Row(3)}"; e.Feed(B(ESC + "[1;1H" + ESC + "[1M")); var afterDl = $"{e.Row(0)}{e.Row(1)}{e.Row(2)}"; return Expect(afterIl == "ABC" && e.Row(1) == "B" || (afterIl == "ABC" && afterDl == "BC"), $"IL='{afterIl}' DL='{afterDl}'"); }),
    ("ED2 / EL", "screen", e => { e.Feed(B("hello\r\nworld" + ESC + "[1;3H" + ESC + "[K")); var r0 = e.Row(0); e.Feed(B(ESC + "[2J")); return Expect(r0 == "he" && e.Row(1) == "", $"EL='{r0}' afterED='{e.Row(1)}'"); }),
    ("DECSC/DECRC", "screen", e => { e.Feed(B(ESC + "[5;10H" + ESC + "7" + ESC + "[1;1H" + ESC + "8X")); return Expect(e.Row(4) == new string(' ', 9) + "X", $"'{e.Row(4)}' cur={e.Cursor}"); }),
    ("SGR truecolor fg", "attrs", e => { e.Feed(B(ESC + "[38;2;1;2;3mX" + ESC + "[0m")); var d = e.FgDescription(0, 0) ?? ""; return Expect(d.Contains("010203", StringComparison.OrdinalIgnoreCase) || d.Contains("1, 2, 3") || d.Contains("#010203"), d); }),
    ("SGR 256 fg", "attrs", e => { e.Feed(B(ESC + "[38;5;196mX" + ESC + "[0m")); var d = e.FgDescription(0, 0) ?? ""; return Expect(d.Contains("196") || d.Contains("C4") || d.Contains("FF0000", StringComparison.OrdinalIgnoreCase), d); }),
    ("OSC 0 title", "osc", e => { e.Feed(B(ESC + "]0;hello\u0007")); return Expect(e.Title == "hello", $"title='{e.Title}'"); }),
    ("OSC 8 hyperlink", "osc", e => { e.Feed(B(ESC + "]8;;https://example.com" + ESC + "\\link" + ESC + "]8;;" + ESC + "\\")); var h = e.HyperlinkAt(0, 1); return Expect(h != null && h.Contains("example.com"), $"link='{h}'"); }),
    ("modes 2004/1/1000/1006", "modes", e => { e.Feed(B(ESC + "[?2004h" + ESC + "[?1h" + ESC + "[?1000h" + ESC + "[?1006h")); return Expect(e.BracketedPaste && e.AppCursorKeys && e.MouseTracking && e.MouseSgr, $"bp={e.BracketedPaste} ac={e.AppCursorKeys} mt={e.MouseTracking} sgr={e.MouseSgr}"); }),
    ("DA1 response (vim needs)", "modes", e => { e.Feed(B(ESC + "[c")); return Expect(e.Responses.Contains("[?"), $"resp='{e.Responses.Replace(ESC, "\\e")}'"); }),
    ("DSR cursor position report", "modes", e => { e.Feed(B(ESC + "[3;7H" + ESC + "[6n")); return Expect(e.Responses.Contains("[3;7R"), $"resp='{e.Responses.Replace(ESC, "\\e")}'"); }),
    ("scrollback keeps lines", "scrollback", e => { var sb = new StringBuilder(); for (int i = 0; i < 1000; i++) sb.Append("line ").Append(i).Append("\r\n"); e.Feed(B(sb.ToString())); return Expect(e.ScrollbackCount >= 976, $"scrollback={e.ScrollbackCount}"); }),
    ("resize reflow wider", "resize", e => { e.Feed(B(new string('y', 100) + "\r\n$ ")); e.Resize(120, 24); return Expect(e.Row(0) == new string('y', 100), $"r0len={e.Row(0).Length} r1len={e.Row(1).Length}"); }),
    ("resize reflow narrower", "resize", e => { e.Feed(B(new string('z', 100) + "\r\n$ ")); e.Resize(40, 24); return Expect(e.Row(0).Length == 40 && e.Row(1).Length == 40 && e.Row(2).Length == 20, $"lens={e.Row(0).Length},{e.Row(1).Length},{e.Row(2).Length}"); }),
};

static string? Expect(bool ok, string observed) => ok ? null : observed;

var results = new Dictionary<string, List<(string Case, string Cat, string? Fail)>>();
foreach (var f in factories)
{
    var name = f().Name;
    results[name] = [];
    foreach (var (cname, cat, run) in cases)
    {
        string? fail;
        try { fail = run(f()); } catch (Exception ex) { fail = "EXCEPTION " + ex.GetType().Name + ": " + ex.Message; }
        results[name].Add((cname, cat, fail));
    }
}

Console.WriteLine("## Conformance\n");
Console.WriteLine("| case | " + string.Join(" | ", results.Keys) + " |");
Console.WriteLine("|---|" + string.Concat(results.Keys.Select(_ => "---|")));
for (int i = 0; i < cases.Count; i++)
    Console.WriteLine($"| {cases[i].Name} | " + string.Join(" | ", results.Values.Select(v => v[i].Fail is null ? "PASS" : "FAIL")) + " |");
Console.WriteLine();
foreach (var (eng, list) in results)
{
    Console.WriteLine($"**{eng}**: {list.Count(x => x.Fail is null)}/{list.Count} pass");
    foreach (var x in list.Where(x => x.Fail is not null)) Console.WriteLine($"  - {x.Case}: {x.Fail}");
}

// ---- Throughput: ~64MB of colored mixed ASCII/CJK text with scrolling (cat of a big colored log)
if (!args.Contains("--no-bench"))
{
    var chunk = BuildBenchChunk();
    int repeat = Math.Max(1, (64 * 1024 * 1024) / chunk.Length);
    Console.WriteLine($"\n## Throughput ({chunk.Length * (long)repeat / 1024 / 1024} MB, 16KB feeds, 120x40)\n");
    foreach (var mk in new Func<IEngine>[] { () => new XTermNetEngine(120, 40), () => new TerminalEmulationEngine(120, 40), () => new VirtualTerminalEngine(120, 40) })
    {
        var e = mk();
        // warmup
        Feed(e, chunk, 1);
        GC.Collect(); GC.WaitForPendingFinalizers();
        long alloc0 = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        Feed(e, chunk, repeat);
        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(true) - alloc0;
        double mb = chunk.Length * (double)repeat / 1024 / 1024;
        Console.WriteLine($"- {e.Name}: {mb / sw.Elapsed.TotalSeconds:F1} MB/s ({sw.ElapsedMilliseconds} ms), allocated {alloc / 1024 / 1024} MB");
    }
}

static void Feed(IEngine e, byte[] data, int repeat)
{
    for (int r = 0; r < repeat; r++)
        for (int off = 0; off < data.Length; off += 16384)
            e.Feed(data.AsSpan(off, Math.Min(16384, data.Length - off)));
}

static byte[] BuildBenchChunk()
{
    var sb = new StringBuilder();
    var rnd = new Random(42);
    string[] words = ["GET", "/api/v1/users", "200", "OK", "错误", "連携", "nginx", "upstream", "timeout", "ms", "接続", "完了"];
    for (int i = 0; i < 4000; i++)
    {
        sb.Append($"\u001b[38;5;{rnd.Next(256)}m{DateTime.UnixEpoch.AddSeconds(i):O}\u001b[0m ");
        int n = rnd.Next(5, 18);
        for (int w = 0; w < n; w++)
        {
            if (rnd.Next(6) == 0) sb.Append($"\u001b[1;38;2;{rnd.Next(256)};{rnd.Next(256)};{rnd.Next(256)}m");
            sb.Append(words[rnd.Next(words.Length)]).Append(' ');
            if (rnd.Next(6) == 0) sb.Append("\u001b[0m");
        }
        sb.Append("\r\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
}

static void DebugReflow()
{
    foreach (var (cols, rows, sb, text) in new[] { (80, 24, 10000, new string('y', 100)), (80, 24, 10000, new string('y', 100) + "\r\nnext"), (80, 5, 10000, string.Join("\r\n", Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), 100)))) })
    {
        var t = new XTerm.Terminal(new XTerm.Options.TerminalOptions { Cols = cols, Rows = rows, Scrollback = sb });
        t.Write(text);
        var b = t.Buffer;
        Console.WriteLine($"before: ybase={b.YBase} y={b.Y} x={b.X} l1wrapped={b.GetLine(b.YBase + 1).IsWrapped} lines={b.Lines.Length}");
        t.Resize(120, rows);
        b = t.Buffer;
        Console.WriteLine($"after 120: resizeCount={b.ResizeCount} ybase={b.YBase} y={b.Y} x={b.X} r0={b.GetLine(b.YBase).TranslateToString(true,0,120).Length} r1={b.GetLine(b.YBase+1).TranslateToString(true,0,120).Length}");
        t.Resize(40, rows);
        b = t.Buffer;
        Console.WriteLine($"after 40: ybase={b.YBase} r0={b.GetLine(b.YBase).TranslateToString(true,0,40).Length} r1={b.GetLine(b.YBase+1).TranslateToString(true,0,40).Length} r2={b.GetLine(b.YBase+2).TranslateToString(true,0,40).Length}");
    }
}
