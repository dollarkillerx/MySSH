using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using MySSH.Desktop.Views;

namespace MySSH.Desktop.Tests;

public sealed class MarkdownTests
{
    const string Answer = """
        我仍然无法访问这台机器：`list_hosts` 返回空，说明它的 **AI 策略还没有设置**。

        ## 下一步
        1. 打开主机设置
        2. 把 AI 策略设为 *ReadOnly*
           之后我就可以直接查询。
        - 也可以手动运行：

        ```bash
        uname -a; free -h
        df -hT | grep -v tmpfs
        ```
        > 注意：不要以 root 运行未知脚本。
        ---
        详见 [文档](https://example.com/docs)。
        """;

    [Fact]
    public void Blocks_are_recognised()
    {
        var blocks = MarkdownParser.Parse(Answer);
        Assert.Equal(
            [MdBlockKind.Paragraph, MdBlockKind.Heading, MdBlockKind.ListItem, MdBlockKind.ListItem, MdBlockKind.ListItem,
             MdBlockKind.Code, MdBlockKind.Quote, MdBlockKind.Rule, MdBlockKind.Paragraph],
            blocks.Select(b => b.Kind));
        Assert.Equal(("1.", "打开主机设置"), (blocks[2].Marker, blocks[2].Text));
        Assert.Equal("把 AI 策略设为 *ReadOnly*\n之后我就可以直接查询。", blocks[3].Text); // continuation line joins the item
        Assert.Equal("•", blocks[4].Marker);
        Assert.Equal(("bash", "uname -a; free -h\ndf -hT | grep -v tmpfs"), (blocks[5].Language, blocks[5].Text));
    }

    [Fact]
    public void An_unterminated_fence_while_streaming_is_code_to_the_end()
    {
        var blocks = MarkdownParser.Parse("Run:\n```sh\nsystemctl status nginx\n");
        Assert.Equal(MdBlockKind.Code, blocks[^1].Kind);
        Assert.Equal("systemctl status nginx\n", blocks[^1].Text);
    }

    [Fact]
    public void Pipe_tables()
    {
        const string md = """
            **203.0.113.10 配置概览**

            | 项目 | 情况 |
            |---|:---|
            | 系统 | Rocky Linux 9.3 |
            | 网络 | eth0：`a|b` IPv6 2a14::3e/64 |
            | 磁盘 | 30 G \| 已用 2.2 G |
            | 缺列 |

            之后的段落
            """;
        var blocks = MarkdownParser.Parse(md);
        Assert.Equal([MdBlockKind.Paragraph, MdBlockKind.Table, MdBlockKind.Paragraph], blocks.Select(b => b.Kind));
        var rows = blocks[1].Rows!;
        Assert.Equal(["项目", "情况"], rows[0]);
        Assert.Equal(["网络", "eth0：`a|b` IPv6 2a14::3e/64"], rows[2]);
        Assert.Equal(["磁盘", "30 G | 已用 2.2 G"], rows[3]);
        Assert.Equal(["缺列", ""], rows[4]);
        // Not a table without the separator line.
        Assert.Equal(MdBlockKind.Paragraph, Assert.Single(MarkdownParser.Parse("| a | b |\n| c | d |")).Kind);
    }

    [Fact]
    public void Inline_spans()
    {
        var spans = MarkdownParser.ParseInline("a `x*y` **b** *c* [d](https://e) f_g_h 2*3*4 \\*lit\\*");
        Assert.Equal(
            [(MdSpanKind.Text, "a "), (MdSpanKind.Code, "x*y"), (MdSpanKind.Text, " "), (MdSpanKind.Bold, "b"), (MdSpanKind.Text, " "),
             (MdSpanKind.Italic, "c"), (MdSpanKind.Text, " "), (MdSpanKind.Link, "d"), (MdSpanKind.Text, " f_g_h 2"), (MdSpanKind.Italic, "3"),
             (MdSpanKind.Text, "4 *lit*")],
            spans.Select(s => (s.Kind, s.Text)));
        Assert.Equal("https://e", spans[7].Url);
    }

    [AvaloniaFact]
    public void Code_blocks_get_copy_and_insert_buttons()
    {
        string? inserted = null;
        var view = new MarkdownView
        {
            Markdown = Answer,
            InsertCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<string?>(c => inserted = c),
        };
        var w = new Window { Content = view, Width = 420, Height = 700 };
        w.Show();
        view.RenderNow();
        var buttons = view.GetLogicalDescendants().OfType<Button>().ToList();
        Assert.Contains(buttons, b => b.Name == "CopyCode");
        var insert = Assert.Single(buttons, b => b.Name == "InsertCode");
        insert.Command!.Execute(insert.CommandParameter);
        Assert.Equal("uname -a; free -h\ndf -hT | grep -v tmpfs", inserted);
        var bold = view.GetLogicalDescendants().OfType<SelectableTextBlock>().First().Inlines!.OfType<Run>().Single(r => r.Text == "AI 策略还没有设置");
        Assert.Equal(FontWeight.SemiBold, bold.FontWeight);
        w.Close();
    }
}
