using System.Globalization;
using System.Text.Json;

namespace MySSH.Tools;

// A tiny JSON Schema model covering exactly what the tool catalog uses (design §6.1): a flat object with
// additionalProperties:false, explicit required, and string (minLength/maxLength/enum) / integer (minimum/maximum) /
// boolean properties with defaults. The same model emits the published schema and validates arguments, so the two
// can never drift. No reflection: AOT-safe.

internal abstract record SchemaProperty(string Name, string Description, bool Required);

internal sealed record StringProperty(
    string Name, string Description, bool Required = false, int MinLength = 0, int? MaxLength = null,
    IReadOnlyList<string>? Enum = null, string? Default = null) : SchemaProperty(Name, Description, Required);

internal sealed record IntegerProperty(
    string Name, string Description, bool Required = false, long? Minimum = null, long? Maximum = null, long? Default = null)
    : SchemaProperty(Name, Description, Required);

internal sealed record BooleanProperty(string Name, string Description, bool Required = false, bool? Default = null)
    : SchemaProperty(Name, Description, Required);

internal sealed class ObjectSchema
{
    public ObjectSchema(params SchemaProperty[] properties)
    {
        Properties = properties;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in properties)
        {
            if (!names.Add(p.Name))
                throw new ArgumentException($"Duplicate schema property '{p.Name}'.", nameof(properties));
            if (p.Required && HasDefault(p))
                throw new ArgumentException($"Required property '{p.Name}' must not have a default.", nameof(properties));
        }
    }

    public IReadOnlyList<SchemaProperty> Properties { get; }

    private static bool HasDefault(SchemaProperty p) => p switch
    {
        StringProperty s => s.Default is not null,
        IntegerProperty i => i.Default is not null,
        BooleanProperty b => b.Default is not null,
        _ => false,
    };

    /// <summary>The JSON Schema document published to MCP / AI providers.</summary>
    public JsonElement ToJsonElement()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "object");
            w.WriteStartObject("properties");
            foreach (var p in Properties)
            {
                w.WriteStartObject(p.Name);
                switch (p)
                {
                    case StringProperty s:
                        w.WriteString("type", "string");
                        w.WriteString("description", s.Description);
                        if (s.Enum is { } values)
                        {
                            w.WriteStartArray("enum");
                            foreach (var v in values) w.WriteStringValue(v);
                            w.WriteEndArray();
                        }
                        if (s.MinLength > 0) w.WriteNumber("minLength", s.MinLength);
                        if (s.MaxLength is { } max) w.WriteNumber("maxLength", max);
                        if (s.Default is { } d) w.WriteString("default", d);
                        break;
                    case IntegerProperty i:
                        w.WriteString("type", "integer");
                        w.WriteString("description", i.Description);
                        if (i.Minimum is { } min) w.WriteNumber("minimum", min);
                        if (i.Maximum is { } imax) w.WriteNumber("maximum", imax);
                        if (i.Default is { } id) w.WriteNumber("default", id);
                        break;
                    case BooleanProperty b:
                        w.WriteString("type", "boolean");
                        w.WriteString("description", b.Description);
                        if (b.Default is { } bd) w.WriteBoolean("default", bd);
                        break;
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartArray("required");
            foreach (var p in Properties.Where(p => p.Required)) w.WriteStringValue(p.Name);
            w.WriteEndArray();
            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Validates <paramref name="arguments"/> (Undefined/Null count as an empty object) and applies defaults.
    /// Returns null and sets <paramref name="error"/> to a precise, model-readable message on failure.
    /// </summary>
    public ValidatedArguments? Validate(JsonElement arguments, out string? error)
    {
        error = null;
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            // no arguments at all
        }
        else if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = $"Arguments must be a JSON object, got {Describe(arguments.ValueKind)}.";
            return null;
        }
        else
        {
            foreach (var prop in arguments.EnumerateObject())
            {
                var def = Properties.FirstOrDefault(p => p.Name == prop.Name);
                if (def is null)
                {
                    error = Properties.Count == 0
                        ? $"Unknown argument '{prop.Name}': this tool takes no arguments."
                        : $"Unknown argument '{prop.Name}'. Allowed arguments: {string.Join(", ", Properties.Select(p => p.Name))}.";
                    return null;
                }
                if (values.ContainsKey(prop.Name))
                {
                    error = $"Argument '{prop.Name}' is given more than once.";
                    return null;
                }
                var v = ValidateValue(def, prop.Value, out error);
                if (v is null)
                    return null;
                values[prop.Name] = v;
            }
        }

        foreach (var p in Properties)
        {
            if (values.ContainsKey(p.Name))
                continue;
            if (p.Required)
            {
                error = $"Missing required argument '{p.Name}' ({p.Description})";
                return null;
            }
            object? d = p switch
            {
                StringProperty s => s.Default,
                IntegerProperty i => i.Default,
                BooleanProperty b => b.Default,
                _ => null,
            };
            if (d is not null)
                values[p.Name] = d;
        }
        return new ValidatedArguments(values);
    }

    private static object? ValidateValue(SchemaProperty def, JsonElement value, out string? error)
    {
        error = null;
        switch (def)
        {
            case StringProperty s:
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    error = $"Argument '{s.Name}' must be a string, got {Describe(value.ValueKind)}.";
                    return null;
                }
                var str = value.GetString()!;
                var length = CodePointLength(str);
                if (length < s.MinLength)
                {
                    error = s.MinLength == 1
                        ? $"Argument '{s.Name}' must not be empty."
                        : $"Argument '{s.Name}' must be at least {s.MinLength} characters long.";
                    return null;
                }
                if (s.MaxLength is { } max && length > max)
                {
                    error = $"Argument '{s.Name}' is too long ({length.ToString(CultureInfo.InvariantCulture)} characters, maximum {max.ToString(CultureInfo.InvariantCulture)}).";
                    return null;
                }
                if (s.Enum is { } allowed && !allowed.Contains(str, StringComparer.Ordinal))
                {
                    error = $"Argument '{s.Name}' must be one of: {string.Join(", ", allowed.Select(a => "\"" + a + "\""))}; got \"{Shorten(str)}\".";
                    return null;
                }
                return str;
            }
            case IntegerProperty i:
            {
                if (value.ValueKind != JsonValueKind.Number)
                {
                    error = $"Argument '{i.Name}' must be an integer, got {Describe(value.ValueKind)}.";
                    return null;
                }
                long n;
                if (!value.TryGetInt64(out n))
                {
                    // Accept integral numbers written as 60.0; reject 1.5 and out-of-range values.
                    var d = value.GetDouble();
                    if (Math.Floor(d) != d || double.IsInfinity(d) || d is > long.MaxValue or < long.MinValue)
                    {
                        error = $"Argument '{i.Name}' must be an integer, got {value.GetRawText()}.";
                        return null;
                    }
                    n = (long)d;
                }
                if (i.Minimum is { } min && n < min)
                {
                    error = $"Argument '{i.Name}' must be >= {min.ToString(CultureInfo.InvariantCulture)}, got {n.ToString(CultureInfo.InvariantCulture)}.";
                    return null;
                }
                if (i.Maximum is { } max && n > max)
                {
                    error = $"Argument '{i.Name}' must be <= {max.ToString(CultureInfo.InvariantCulture)}, got {n.ToString(CultureInfo.InvariantCulture)}.";
                    return null;
                }
                return n;
            }
            case BooleanProperty b:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = $"Argument '{b.Name}' must be a boolean (true or false), got {Describe(value.ValueKind)}.";
                    return null;
                }
                return value.GetBoolean();
            default:
                error = $"Argument '{def.Name}' has an unsupported schema type.";
                return null;
        }
    }

    private static int CodePointLength(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };
}

/// <summary>Arguments after validation, with defaults applied.</summary>
internal sealed class ValidatedArguments(Dictionary<string, object> values)
{
    public static readonly ValidatedArguments Empty = new([]);

    public bool Has(string name) => values.ContainsKey(name);

    public string? String(string name) => values.TryGetValue(name, out var v) ? (string)v : null;

    public string RequiredString(string name) => String(name) ?? throw new InvalidOperationException($"Argument '{name}' was not validated.");

    public long? Integer(string name) => values.TryGetValue(name, out var v) ? (long)v : null;

    public bool? Boolean(string name) => values.TryGetValue(name, out var v) ? (bool)v : null;
}
