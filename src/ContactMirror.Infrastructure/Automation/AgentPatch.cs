using System.Text.Json.Nodes;
using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Automation;

internal static class AgentPatch
{
    public static JsonObject Apply(JsonObject original, JsonArray patches)
    {
        if (patches.Count > 64) throw new SyncException("invalid_patch", "Допустимо не более 64 изменений.");
        var doc = (JsonObject)original.DeepClone();
        foreach (var node in patches)
        {
            if (node is not JsonObject patch || patch.Any(p => p.Key is not ("op" or "path" or "value"))) throw new SyncException("invalid_patch", "Ожидаются op/path/value.");
            var op = patch["op"]?.GetValue<string>(); var path = patch["path"]?.GetValue<string>() ?? "";
            if (!path.StartsWith('/') || path.Length > 1024) throw new SyncException("invalid_patch", "Используйте JSON Pointer.");
            var segments = path[1..].Split('/').Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            if (segments[0] is not ("data" or "labels" or "starred" or "extensions")) throw new SyncException("protectedField", "Служебные поля не редактируются.");
            JsonNode current = doc;
            foreach (var segment in segments[..^1]) current = Child(current, segment) ?? throw new SyncException("invalid_patch", "Родительский путь отсутствует; сначала добавьте массив или объект.");
            var key = segments[^1]; var value = patch["value"]?.DeepClone();
            if (op != "remove" && !patch.ContainsKey("value")) throw new SyncException("invalid_patch", "Укажите value.");
            if (current is JsonObject obj)
            {
                if (op is "replace" or "remove" && !obj.ContainsKey(key)) throw new SyncException("invalid_patch", "Заменяемый путь отсутствует.");
                if (op == "remove") obj.Remove(key); else if (op is "add" or "replace") obj[key] = value; else throw new SyncException("invalid_patch", "Неизвестная операция patch.");
            }
            else if (current is JsonArray array)
            {
                var index = key == "-" && op == "add" ? array.Count : int.TryParse(key, out var i) && i >= 0 ? i : -1;
                if (index < 0 || index > array.Count || op != "add" && index == array.Count) throw new SyncException("invalid_patch", "Неверный индекс массива.");
                if (op == "add") array.Insert(index, value); else if (op == "replace") array[index] = value; else if (op == "remove") array.RemoveAt(index); else throw new SyncException("invalid_patch", "Неизвестная операция patch.");
            }
            else throw new SyncException("invalid_patch", "Путь не является контейнером.");
        }
        if (JsonSemantics.Serialize(doc).Length > 16 * 1024 * 1024) throw new SyncException("resource_limit", "Документ превышает 16 МБ.");
        return doc;
    }
    private static JsonNode? Child(JsonNode node, string key) => node switch
    {
        JsonObject o => o[key], JsonArray a when int.TryParse(key, out var i) && i >= 0 && i < a.Count => a[i], _ => null
    };
}
