using System.Text.Json;
using System.Text.Json.Nodes;

namespace FCon.Abstractions.Emit;

public static class JsonKit
{
    public static JsonObject Obj() => new();

    public static JsonArray ArrayOf(IEnumerable<string> values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(v);
        return a;
    }

    /// <summary>Set only when the value is non-empty — keeps emitted configs free of noise.</summary>
    public static void SetIf(this JsonObject o, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) o[key] = value;
    }

    public static void SetIf(this JsonObject o, string key, bool value)
    {
        if (value) o[key] = true;
    }

    public static void SetIfPositive(this JsonObject o, string key, int value)
    {
        if (value > 0) o[key] = value;
    }

    public static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Deep-merge <paramref name="patch"/> into <paramref name="target"/>.</summary>
    public static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject po && target[key] is JsonObject to) Merge(to, po);
            else target[key] = value?.DeepClone();
        }
    }
}
