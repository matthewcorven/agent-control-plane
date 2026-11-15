using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AGUIWebChat.Client.Services;

public static class ScenarioStatePatch
{
    public static JsonObject Apply(JsonObject current, JsonElement delta)
    {
        if (delta.ValueKind != JsonValueKind.Array || delta.GetArrayLength() > 128)
            throw new InvalidOperationException("State delta must be a bounded JSON Patch array.");
        JsonNode? candidate = current.DeepClone();
        foreach (var operation in delta.EnumerateArray())
        {
            string op = RequiredString(operation, "op");
            string[] path = Pointer(RequiredString(operation, "path"));
            switch (op)
            {
                case "test":
                    if (!JsonNode.DeepEquals(Resolve(candidate, path), RequiredValue(operation)))
                        throw new InvalidOperationException("The document changed after this proposal. Request a new proposal.");
                    break;
                case "add": case "replace":
                    Set(ref candidate, path, RequiredValue(operation), op == "add");
                    break;
                case "remove":
                    Remove(ref candidate, path);
                    break;
                case "copy": case "move":
                    string[] from = Pointer(RequiredString(operation, "from"));
                    JsonNode? value = Resolve(candidate, from)?.DeepClone();
                    if (op == "move")
                    {
                        if (from.SequenceEqual(path)) break;
                        if (path.Length > from.Length && path.Take(from.Length).SequenceEqual(from))
                            throw new InvalidOperationException("A state patch cannot move a value into its own child.");
                        Remove(ref candidate, from);
                    }
                    Set(ref candidate, path, value, true);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported state patch operation.");
            }
            if (Encoding.UTF8.GetByteCount(candidate?.ToJsonString() ?? "null") > 256 * 1024)
                throw new InvalidOperationException("State patch exceeds the conversation limit.");
        }
        return candidate as JsonObject ?? throw new InvalidOperationException("The scenario root state must remain an object.");
    }

    private static string RequiredString(JsonElement operation, string property) =>
        operation.ValueKind == JsonValueKind.Object &&
        operation.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidOperationException("State patch is missing a required string.");

    private static JsonNode? RequiredValue(JsonElement operation) =>
        operation.TryGetProperty("value", out var value) ? JsonNode.Parse(value.GetRawText()) :
            throw new InvalidOperationException("State patch is missing its value.");

    private static JsonNode? Resolve(JsonNode? root, string[] segments)
    {
        JsonNode? node = root;
        foreach (string segment in segments)
        {
            node = node switch
            {
                JsonObject obj when obj.TryGetPropertyValue(segment, out var child) => child,
                JsonArray array => array[Index(segment, array.Count, false)],
                _ => throw new InvalidOperationException("State patch target does not exist.")
            };
        }
        return node;
    }

    private static void Set(ref JsonNode? root, string[] path, JsonNode? value, bool add)
    {
        if (path.Length == 0) { root = value?.DeepClone(); return; }
        JsonNode? parent = Resolve(root, path[..^1]);
        string leaf = path[^1];
        if (parent is JsonObject obj)
        {
            if (!add && !obj.ContainsKey(leaf)) throw new InvalidOperationException("State patch target does not exist.");
            obj[leaf] = value?.DeepClone();
        }
        else if (parent is JsonArray array)
        {
            int at = Index(leaf, array.Count, add);
            if (add) array.Insert(at, value?.DeepClone());
            else array[at] = value?.DeepClone();
        }
        else throw new InvalidOperationException("State patch parent is not a collection.");
    }

    private static void Remove(ref JsonNode? root, string[] path)
    {
        if (path.Length == 0) { root = null; return; }
        JsonNode? parent = Resolve(root, path[..^1]);
        string leaf = path[^1];
        if (parent is JsonObject obj)
        {
            if (!obj.Remove(leaf)) throw new InvalidOperationException("State patch target does not exist.");
        }
        else if (parent is JsonArray array) array.RemoveAt(Index(leaf, array.Count, false));
        else throw new InvalidOperationException("State patch parent is not a collection.");
    }

    private static int Index(string segment, int count, bool append)
    {
        if (segment == "-" && append) return count;
        if (segment.Length == 0 || (segment.Length > 1 && segment[0] == '0') ||
            segment.Any(character => character is < '0' or > '9') ||
            !int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int at) ||
            at < 0 || at > count || (!append && at == count))
            throw new InvalidOperationException("State patch array index is invalid.");
        return at;
    }

    private static string[] Pointer(string path)
    {
        if (path.Length == 0) return [];
        if (!path.StartsWith('/')) throw new InvalidOperationException("Invalid state patch path.");
        return path[1..].Split('/').Select(segment =>
        {
            for (int i = 0; i < segment.Length; i++)
                if (segment[i] == '~' && (i + 1 == segment.Length || segment[++i] is not ('0' or '1')))
                    throw new InvalidOperationException("Invalid JSON pointer escape.");
            return segment.Replace("~1", "/").Replace("~0", "~");
        }).ToArray();
    }
}
