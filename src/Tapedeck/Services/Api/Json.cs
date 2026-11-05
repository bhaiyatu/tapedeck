using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tapedeck.Services.Api;

/// <summary>Null-safe navigation helpers for InnerTube's deeply nested JSON.</summary>
internal static class Json
{
    /// <summary>Follows a path of object keys (string) and array indices (int).</summary>
    public static JsonNode? At(this JsonNode? node, params object[] path)
    {
        var cur = node;
        foreach (var step in path)
        {
            if (cur is null) return null;
            cur = step switch
            {
                string key when cur is JsonObject o => o.TryGetPropertyValue(key, out var v) ? v : null,
                int i when cur is JsonArray a => i >= 0 && i < a.Count ? a[i] : null,
                _ => null,
            };
        }
        return cur;
    }

    public static string? Str(this JsonNode? node, params object[] path)
    {
        var n = node.At(path);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return v.ToJsonString();
        }
        return null;
    }

    public static long? Long(this JsonNode? node, params object[] path)
    {
        var n = node.At(path);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
        }
        return null;
    }

    public static JsonArray Arr(this JsonNode? node, params object[] path) => node.At(path) as JsonArray ?? [];

    public static JsonObject? Obj(this JsonNode? node, params object[] path) => node.At(path) as JsonObject;

    /// <summary>Joins the "runs" of a formatted text object (or returns its simpleText).</summary>
    public static string Text(this JsonNode? textNode)
    {
        if (textNode is null) return "";
        var simple = textNode.Str("simpleText");
        if (simple is not null) return simple;
        var runs = textNode.Arr("runs");
        return string.Concat(runs.Select(r => r.Str("text") ?? ""));
    }

    public static JsonArray Runs(this JsonNode? textNode) => textNode.Arr("runs");

    /// <summary>Returns the first (key, value) of an object, used to unwrap {"someRenderer": {...}}.</summary>
    public static (string Key, JsonNode? Value) Single(this JsonNode? node)
    {
        if (node is JsonObject o)
            foreach (var kv in o) return (kv.Key, kv.Value);
        return ("", null);
    }

    /// <summary>Depth-first search for every value stored under <paramref name="key"/>.</summary>
    public static IEnumerable<JsonNode> FindAll(this JsonNode? root, string key)
    {
        if (root is null) yield break;
        var stack = new Stack<JsonNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is JsonObject o)
            {
                // Reverse push keeps document order when popping.
                var children = new List<JsonNode>();
                foreach (var kv in o)
                {
                    if (kv.Value is null) continue;
                    if (kv.Key == key) yield return kv.Value;
                    children.Add(kv.Value);
                }
                for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            else if (n is JsonArray a)
            {
                for (var i = a.Count - 1; i >= 0; i--)
                    if (a[i] is { } c) stack.Push(c);
            }
        }
    }

    public static JsonNode? FindFirst(this JsonNode? root, string key) => root.FindAll(key).FirstOrDefault();

    /// <summary>Picks the largest thumbnail URL from a {thumbnails:[...]} list found anywhere under the node.</summary>
    public static string? Thumbnail(this JsonNode? node)
    {
        var list = node.FindFirst("thumbnails") as JsonArray;
        if (list is null || list.Count == 0) return null;
        JsonNode? best = null;
        long bestW = -1;
        foreach (var t in list)
        {
            var w = t.Long("width") ?? 0;
            if (w >= bestW) { bestW = w; best = t; }
        }
        var url = best.Str("url");
        if (url is not null && url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        return url;
    }

    public static JsonObject? CloneObj(this JsonNode? node) => node?.DeepClone() as JsonObject;
}
