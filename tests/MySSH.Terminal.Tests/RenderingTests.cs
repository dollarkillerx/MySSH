using Avalonia.Headless.XUnit;
using Avalonia.Styling;

namespace MySSH.Terminal.Tests;

public class RenderingTests
{
    private const string E = Harness.E;

    internal static readonly string Sample = string.Join("\r\n",
    [
        $"{E}[1;32mdeploy@prod-web-01{E}[0m:{E}[1;34m~/app{E}[0m$ ls -la",
        $"drwxr-xr-x  5 deploy deploy 4096 11-15 09:12 {E}[1;34mconfig{E}[0m   {E}[32mrun.sh{E}[0m   {E}[31marchive.tar.gz{E}[0m",
        "中文测试：服务器连接成功，延迟 23ms。日本語：接続が完了しました。한국어도 OK",
        "全角ＡＢＣ半角ABC｜emoji 👍🚀 ✅  ZWJ 👨‍👩‍👧 flag 🇯🇵 combining: é ä ñ",
        $"{E}[1mbold{E}[0m {E}[3mitalic{E}[0m {E}[4munderline{E}[0m {E}[4:3mcurly{E}[0m {E}[21mdouble{E}[0m {E}[9mstrike{E}[0m {E}[7minverse{E}[0m {E}[2mdim{E}[0m {E}[4;58;2;255;0;0mred-ul{E}[0m",
        string.Concat(Enumerable.Range(0, 16).Select(i => $"{E}[48;5;{i}m  ")) + $"{E}[0m " + string.Concat(Enumerable.Range(232, 24).Select(i => $"{E}[48;5;{i}m ")) + $"{E}[0m",
        string.Concat(Enumerable.Range(0, 72).Select(i => $"{E}[48;2;{i * 3};{80 + i};{255 - i * 3}m ")) + $"{E}[0m",
        string.Concat(Enumerable.Range(0, 16).Select(i => $"{E}[38;5;{i}m██")) + $"{E}[0m " + string.Concat(Enumerable.Range(1, 7).Select(i => $"{E}[3{i}mAa{E}[9{i}mAa")) + $"{E}[0m",
        $"{E}[7m NORMAL {E}[0m{E}[48;5;238m nginx.conf [+]                                  utf-8  42:7 {E}[0m",
        $"{E}[1;32mdeploy@prod-web-01{E}[0m:{E}[1;34m~/app{E}[0m$ echo 你好",
    ]);

    internal static readonly string Boxes = string.Join("\r\n",
    [
        "┌──────────┬──────────┐ ╔════╦════╗ ┏━━━━┳━━━━┓ ╭────╮",
        "│ 主机     │ 状态     │ ║ ab ║ cd ║ ┃ ab ┃ cd ┃ │ ok │",
        "├──────────┼──────────┤ ╠════╬════╣ ┣━━━━╋━━━━┫ ╰────╯",
        $"│ bastion  │ {E}[42;30m 在线 {E}[0m   │ ║ ef ║ gh ║ ┃ ef ┃ gh ┃ ┄┄┄ ┅┅┅ ╌╌",
        "└──────────┴──────────┘ ╚════╩════╝ ┗━━━━┻━━━━┛ ╒══╕╓──╖",
        "▀▁▂▃▄▅▆▇█▉▊▋▌▍▎▏▐░▒▓▔▕▖▗▘▙▚▛▜▝▞▟ ╱╲╳ ├┤┬┴┼ ╞╡╟╢",
        $"{E}[44;97m main {E}[34;42m{E}[30;42m src/app {E}[32;49m{E}[0m  {E}[45m x {E}[35;43m{E}[0m  ",
        "████████████████████",
        "████████████████████",
    ]);

    [AvaloniaFact]
    public void Mixed_content_dark()
    {
        var h = new Harness();
        h.Feed(Sample);
        h.Save("01-content-dark");
        h.SaveZoom("01z-emoji", 150, 55, 330, 25, 4);
        Assert.Contains("中文测试", h.Screen(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Mixed_content_light()
    {
        var h = new Harness(theme: ThemeVariant.Light);
        h.Feed(Sample);
        h.Save("02-content-light");
        Assert.True(h.View.ResolvedTheme!.IsLight);
    }

    [AvaloniaFact]
    public void Box_drawing_dark()
    {
        var h = new Harness(width: 700, height: 260);
        h.Feed(Boxes);
        h.Save("03-boxes-dark");
        h.SaveZoom("03z-boxes", 190, 0, 280, 100, 4);
        h.SaveZoom("03z-powerline", 0, 114, 260, 20, 5);
    }
}
