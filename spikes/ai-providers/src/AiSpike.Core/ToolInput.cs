using System.Text.Json;

namespace AiSpike.Core;

/// <summary>
/// Strict parse + minimal JSON-Schema validation of streamed tool input (eager_input_streaming means
/// the server no longer validates). Spike-only subset: type, properties, required,
/// additionalProperties:false, enum, minimum/maximum. Production: use a real validator
/// (e.g. JsonSchema.Net) driven by the same ToolCatalog schema.
/// </summary>
public static class ToolInput
{
    public static ToolCall Build(string id, string name, string rawInput, IReadOnlyList<ToolDefinition> tools)
    {
        var def = tools.FirstOrDefault(t => t.Name == name);
        if (def is null)
            return new ToolCall(id, name, null, rawInput, $"unknown tool '{name}'");

        JsonElement input;
        try
        {
            // Strict parse: no trailing commas, no comments. An empty buffer means "{}" only if the
            // schema has no required props - we treat it as {} and let validation decide.
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawInput) ? "{}" : rawInput);
            input = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new ToolCall(id, name, null, rawInput, InvalidJson(rawInput));
        }

        var errors = new List<string>();
        Validate(input, def.InputSchema, "$", errors);
        return errors.Count == 0
            ? new ToolCall(id, name, input, rawInput, null)
            : new ToolCall(id, name, input, rawInput, "schema validation failed: " + string.Join("; ", errors));
    }

    /// <summary>{"INVALID_JSON": "&lt;raw&gt;"} built with the JSON library (escaping handled).</summary>
    public static string InvalidJson(string raw) => JsonSerializer.Serialize(new Dictionary<string, string> { ["INVALID_JSON"] = raw });

    public static void Validate(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            var type = typeEl.GetString();
            bool ok = type switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => true,
            };
            if (!ok) { errors.Add($"{path}: expected {type}"); return; }
        }

        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            if (!enumEl.EnumerateArray().Any(e => JsonElement.DeepEquals(e, value)))
                errors.Add($"{path}: not in enum");
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            var d = value.GetDouble();
            if (schema.TryGetProperty("minimum", out var min) && d < min.GetDouble()) errors.Add($"{path}: < minimum");
            if (schema.TryGetProperty("maximum", out var max) && d > max.GetDouble()) errors.Add($"{path}: > maximum");
        }

        if (value.ValueKind != JsonValueKind.Object) return;

        schema.TryGetProperty("properties", out var props);
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            foreach (var r in req.EnumerateArray())
                if (!value.TryGetProperty(r.GetString()!, out _)) errors.Add($"{path}.{r.GetString()}: required");

        bool noExtra = schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind == JsonValueKind.False;
        foreach (var p in value.EnumerateObject())
        {
            if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty(p.Name, out var sub))
                Validate(p.Value, sub, $"{path}.{p.Name}", errors);
            else if (noExtra)
                errors.Add($"{path}.{p.Name}: additional property not allowed");
        }
    }
}
