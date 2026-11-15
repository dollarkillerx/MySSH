using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using TermView = TermControl.TerminalControl;

// Headless Skia rendering of the spike TerminalControl: writes PNGs for visual review and measures frame cost.
var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outDir);

AppBuilder.Configure<HeadlessApp>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

const string E = "\u001b";
string sample = string.Join("\r\n",
[
    $"{E}[1;32mdeploy@prod-web-01{E}[0m:{E}[1;34m~/app{E}[0m$ ls -la",
    $"drwxr-xr-x  5 deploy deploy 4096 11-15 09:12 {E}[1;34mconfig{E}[0m   {E}[32mrun.sh{E}[0m   {E}[31marchive.tar.gz{E}[0m",
    $"中文测试：服务器连接成功，延迟 23ms。日本語：接続が完了しました。한국어도 OK",
    $"全角ＡＢＣ半角ABC｜emoji 👍🚀 ✅  ZWJ 👨‍👩‍👧  combining: é ä",
    "┌──────────┬──────────┐",
    "│ 主机     │ 状态     │",
    "├──────────┼──────────┤",
    $"│ bastion  │ {E}[42;30m 在线 {E}[0m     │",
    "└──────────┴──────────┘",
    $"{E}[1mbold{E}[0m {E}[3mitalic{E}[0m {E}[4munderline{E}[0m {E}[9mstrike{E}[0m {E}[7minverse{E}[0m {E}[2mdim{E}[0m",
    string.Concat(Enumerable.Range(0, 16).Select(i => $"{E}[48;5;{i}m  ")) + $"{E}[0m " + string.Concat(Enumerable.Range(232, 24).Select(i => $"{E}[48;5;{i}m "))+ $"{E}[0m",
    string.Concat(Enumerable.Range(0, 72).Select(i => $"{E}[48;2;{i * 3};{80 + i};{255 - i * 3}m ")) + $"{E}[0m",
    $"{E}[7m NORMAL {E}[0m{E}[48;5;238m nginx.conf [+]                                  utf-8  42:7 {E}[0m",
    $"{E}[1;32mdeploy@prod-web-01{E}[0m:{E}[1;34m~/app{E}[0m$ echo 你好",
]);

var results = new StringBuilder();
// SetupWithoutStarting: the main thread is the UI thread; drive it synchronously with RunJobs().
{
    var term = new TermView { TerminalFontSize = 14 };
    var win = new Window { Width = 900, Height = 420, Content = term };
    win.Show();
    Dispatcher.UIThread.RunJobs();
    term.Focus();
    term.FeedNow(Encoding.UTF8.GetBytes(sample));
    Dispatcher.UIThread.RunJobs();
    Save(win, Path.Combine(outDir, "01-content.png"));
    results.AppendLine($"grid {term.Engine.Cols}x{term.Engine.Rows}, cursor=({term.Engine.Buffer.Y},{term.Engine.Buffer.X})");

    // IME preedit inline at the cursor (Chinese pinyin composition / Japanese)
    term.DebugSetPreedit("ni hao");
    Dispatcher.UIThread.RunJobs();
    Save(win, Path.Combine(outDir, "02-preedit-pinyin.png"));
    term.DebugSetPreedit("にほんご");
    Dispatcher.UIThread.RunJobs();
    Save(win, Path.Combine(outDir, "03-preedit-kana.png"));
    term.DebugSetPreedit(null);

    // alt screen app (vim-ish) + selection-free check of GetVisibleText
    term.FeedNow(Encoding.UTF8.GetBytes($"{E}[?1049h{E}[H{E}[2J{E}[38;5;244m  1 {E}[0mserver {{\r\n{E}[38;5;244m  2 {E}[0m    listen 443 ssl;   # 中文注释\r\n{E}[38;5;244m  3 {E}[0m}}\r\n~\r\n~\r\n{E}[{term.Engine.Rows};1H{E}[7m-- INSERT --{E}[0m{E}[2;20H"));
    Dispatcher.UIThread.RunJobs();
    Save(win, Path.Combine(outDir, "04-altscreen.png"));
    results.AppendLine("visible text (alt):\n" + term.GetVisibleText(4));
    term.FeedNow(Encoding.UTF8.GetBytes($"{E}[?1049l"));

    // ---- frame cost on a dense 160x50 screen, full repaint each frame
    var big = new TermView { TerminalFontSize = 13 };
    var win2 = new Window { Width = 1400, Height = 900, Content = big };
    win2.Show();
    Dispatcher.UIThread.RunJobs();
    var rnd = new Random(1);
    string[] words = ["GET", "/api/v1/users", "200", "OK", "错误", "連携", "nginx", "upstream", "timeout", "接続", "完了", "│", "─"];
    var times = new List<double>();
    long bytes = 0;
    var total = Stopwatch.StartNew();
    for (int frame = 0; frame < 120; frame++)
    {
        var sb = new StringBuilder();
        for (int l = 0; l < 30; l++)
        {
            for (int w = 0; w < 14; w++) sb.Append($"{E}[38;5;{rnd.Next(256)}m{words[rnd.Next(words.Length)]} ");
            sb.Append($"{E}[0m\r\n");
        }
        var data = Encoding.UTF8.GetBytes(sb.ToString());
        bytes += data.Length;
        big.FeedNow(data);
        var sw = Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        win2.CaptureRenderedFrame(); // forces a real Skia render of the frame
        times.Add(sw.Elapsed.TotalMilliseconds);
    }
    Save(win2, Path.Combine(outDir, "05-dense.png"));
    times.Sort();
    results.AppendLine($"dense {big.Engine.Cols}x{big.Engine.Rows}: frame (render+capture) p50={times[times.Count / 2]:F1}ms p95={times[(int)(times.Count * 0.95)]:F1}ms max={times[^1]:F1}ms; Render() itself last={big.LastRenderTime.TotalMilliseconds:F1}ms; {bytes / 1024}KB in {total.ElapsedMilliseconds}ms");
}

results.AppendLine(PtySmoke.Run(Path.GetFullPath(outDir)));
Console.WriteLine(results);
Console.WriteLine($"PNGs: {outDir}");

static void Save(Window w, string path)
{
    var frame = w.CaptureRenderedFrame();
    frame?.Save(path);
}

sealed class HeadlessApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
}
