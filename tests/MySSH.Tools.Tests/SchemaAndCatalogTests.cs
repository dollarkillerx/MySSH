using System.Text.Json;

namespace MySSH.Tools.Tests;

public class SchemaValidatorTests
{
    private static readonly ObjectSchema s_schema = new(
        new StringProperty("host", "host", Required: true, MinLength: 1, MaxLength: 5),
        new StringProperty("mode", "mode", Enum: ["create", "overwrite"], Default: "create"),
        new IntegerProperty("n", "n", Minimum: 1, Maximum: 10, Default: 3),
        new BooleanProperty("flag", "flag", Default: false),
        new StringProperty("opt", "opt"));

    private static (ValidatedArguments? Args, string? Error) Validate(string json)
    {
        var a = s_schema.Validate(T.Json(json), out var e);
        return (a, e);
    }

    [Fact]
    public void Valid_arguments_get_defaults()
    {
        var (a, e) = Validate("""{"host":"web"}""");
        Assert.Null(e);
        Assert.Equal("web", a!.String("host"));
        Assert.Equal("create", a.String("mode"));
        Assert.Equal(3, a.Integer("n"));
        Assert.False(a.Boolean("flag"));
        Assert.False(a.Has("opt"));
        Assert.Null(a.String("opt"));

        (a, e) = Validate("""{"host":"web","mode":"overwrite","n":10,"flag":true,"opt":""}""");
        Assert.Null(e);
        Assert.Equal(("overwrite", 10L, true, ""), (a!.String("mode"), a.Integer("n"), a.Boolean("flag"), a.String("opt")));
    }

    [Theory]
    [InlineData("""{}""", "Missing required argument 'host'")]
    [InlineData("""{"host":""}""", "Argument 'host' must not be empty.")]
    [InlineData("""{"host":"toolong"}""", "Argument 'host' is too long (7 characters, maximum 5).")]
    [InlineData("""{"host":"web","extra":1}""", "Unknown argument 'extra'. Allowed arguments: host, mode, n, flag, opt.")]
    [InlineData("""{"host":1}""", "Argument 'host' must be a string, got a number.")]
    [InlineData("""{"host":null}""", "Argument 'host' must be a string, got null.")]
    [InlineData("""{"host":"web","mode":"append"}""", "Argument 'mode' must be one of: \"create\", \"overwrite\"; got \"append\".")]
    [InlineData("""{"host":"web","n":0}""", "Argument 'n' must be >= 1, got 0.")]
    [InlineData("""{"host":"web","n":11}""", "Argument 'n' must be <= 10, got 11.")]
    [InlineData("""{"host":"web","n":1.5}""", "Argument 'n' must be an integer, got 1.5.")]
    [InlineData("""{"host":"web","n":"3"}""", "Argument 'n' must be an integer, got a string.")]
    [InlineData("""{"host":"web","n":1e30}""", "Argument 'n' must be an integer, got 1e30.")]
    [InlineData("""{"host":"web","flag":"true"}""", "Argument 'flag' must be a boolean (true or false), got a string.")]
    [InlineData("""{"host":"web","flag":1}""", "Argument 'flag' must be a boolean (true or false), got a number.")]
    [InlineData("""{"host":"web","host":"x"}""", "Argument 'host' is given more than once.")]
    [InlineData("""[]""", "Arguments must be a JSON object, got an array.")]
    [InlineData("\"x\"", "Arguments must be a JSON object, got a string.")]
    public void Invalid_arguments_have_precise_messages(string json, string expected)
    {
        var (a, e) = Validate(json);
        Assert.Null(a);
        Assert.StartsWith(expected, e, StringComparison.Ordinal);
    }

    [Fact]
    public void Integral_float_is_accepted_and_lengths_count_code_points()
    {
        var (a, e) = Validate("""{"host":"web","n":4.0}""");
        Assert.Null(e);
        Assert.Equal(4, a!.Integer("n"));
        (a, e) = Validate("""{"host":"😀😀😀😀😀"}"""); // 5 code points, 10 UTF-16 units
        Assert.Null(e);
        (a, _) = Validate("""{"host":"😀😀😀😀😀😀"}""");
        Assert.Null(a);
    }

    [Fact]
    public void Missing_or_null_arguments_count_as_empty_object()
    {
        var empty = new ObjectSchema(new StringProperty("q", "q"));
        Assert.NotNull(empty.Validate(default, out _));
        Assert.NotNull(empty.Validate(T.Json("null"), out _));
        Assert.Null(new ObjectSchema().Validate(T.Json("""{"x":1}"""), out var e));
        Assert.Equal("Unknown argument 'x': this tool takes no arguments.", e);
    }

    [Fact]
    public void Schema_definition_errors_are_caught()
    {
        Assert.Throws<ArgumentException>(() => new ObjectSchema(new StringProperty("a", "a"), new StringProperty("a", "b")));
        Assert.Throws<ArgumentException>(() => new ObjectSchema(new StringProperty("a", "a", Required: true, Default: "x")));
    }

    [Fact]
    public void Emitted_schema_matches_the_model()
    {
        var s = s_schema.ToJsonElement();
        Assert.Equal("object", s.GetProperty("type").GetString());
        Assert.False(s.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["host"], s.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var host = s.GetProperty("properties").GetProperty("host");
        Assert.Equal(("string", 1, 5), (host.GetProperty("type").GetString(), host.GetProperty("minLength").GetInt32(), host.GetProperty("maxLength").GetInt32()));
        var mode = s.GetProperty("properties").GetProperty("mode");
        Assert.Equal(["create", "overwrite"], mode.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("create", mode.GetProperty("default").GetString());
        var n = s.GetProperty("properties").GetProperty("n");
        Assert.Equal(("integer", 1, 10, 3), (n.GetProperty("type").GetString(), n.GetProperty("minimum").GetInt32(), n.GetProperty("maximum").GetInt32(), n.GetProperty("default").GetInt32()));
        Assert.Equal("boolean", s.GetProperty("properties").GetProperty("flag").GetProperty("type").GetString());
    }
}

public class CatalogTests
{
    private static readonly ToolCatalog s_catalog = new();

    [Fact]
    public void Catalog_has_the_design_tools_with_annotations()
    {
        Assert.Equal(
            ["list_hosts", "run_command", "sftp_list", "sftp_stat", "sftp_read", "sftp_write", "sftp_mkdir", "sftp_delete", "list_sessions", "read_terminal", "send_to_terminal"],
            s_catalog.Tools.Select(t => t.Name));
        foreach (var t in s_catalog.Tools)
        {
            var expectReadOnly = t.Capability is ToolCapability.Read or ToolCapability.TerminalRead;
            Assert.Equal(expectReadOnly, t.ReadOnly);
            Assert.Equal(!expectReadOnly, t.Destructive);
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.True(t.Description.Length > 40, t.Name);
            Assert.Same(s_catalog.Find(t.Name), t);
        }
        Assert.Null(s_catalog.Find("LIST_HOSTS"));
        Assert.Null(s_catalog.Find("nope"));
        Assert.Equal(ToolCapability.Execute, s_catalog.Find("run_command")!.Capability);
        Assert.Equal(ToolCapability.Write, s_catalog.Find("sftp_delete")!.Capability);
        Assert.Equal(ToolCapability.TerminalWrite, s_catalog.Find("send_to_terminal")!.Capability);
    }

    [Fact]
    public void Every_schema_is_a_closed_object_with_explicit_required_and_descriptions()
    {
        foreach (var t in s_catalog.Tools)
        {
            var s = t.InputSchema;
            Assert.Equal("object", s.GetProperty("type").GetString());
            Assert.False(s.GetProperty("additionalProperties").GetBoolean(), t.Name);
            Assert.Equal(JsonValueKind.Array, s.GetProperty("required").ValueKind);
            var props = s.GetProperty("properties").EnumerateObject().ToList();
            foreach (var p in props)
                Assert.False(string.IsNullOrWhiteSpace(p.Value.GetProperty("description").GetString()), $"{t.Name}.{p.Name}");
            foreach (var r in s.GetProperty("required").EnumerateArray())
                Assert.Contains(props, p => p.Name == r.GetString());
        }
    }

    [Fact]
    public void Limits_come_from_options()
    {
        var c = new ToolCatalog(new ToolOptions { MaxCommandTimeout = TimeSpan.FromSeconds(30), DefaultCommandTimeout = TimeSpan.FromSeconds(90), MaxTerminalLines = 50, MaxSftpReadBytes = 4096, DefaultSftpReadBytes = 1024 });
        var timeout = c.Find("run_command")!.InputSchema.GetProperty("properties").GetProperty("timeout_s");
        Assert.Equal((30, 30), (timeout.GetProperty("maximum").GetInt32(), timeout.GetProperty("default").GetInt32())); // default clamped
        var lines = c.Find("read_terminal")!.InputSchema.GetProperty("properties").GetProperty("lines");
        Assert.Equal((50, 50), (lines.GetProperty("maximum").GetInt32(), lines.GetProperty("default").GetInt32()));
        var max = c.Find("sftp_read")!.InputSchema.GetProperty("properties").GetProperty("max_bytes");
        Assert.Equal((4096, 1024), (max.GetProperty("maximum").GetInt32(), max.GetProperty("default").GetInt32()));
        var d = s_catalog.Find("run_command")!.InputSchema.GetProperty("properties").GetProperty("timeout_s");
        Assert.Equal((600, 60), (d.GetProperty("maximum").GetInt32(), d.GetProperty("default").GetInt32()));
        Assert.Equal(new[] { "host", "command" }, s_catalog.Find("run_command")!.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }
}
