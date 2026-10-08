using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ContactMirror.Core;

public sealed record DiffRow(string Section, string Title, string Path, string Before, string Local, string Google,
    string? BeforePath = null, string? LocalPath = null, string? GooglePath = null)
{
    public bool BeforeIsJson { get; init; }
    public bool LocalIsJson { get; init; }
    public bool GoogleIsJson { get; init; }
    public string DisplayPath => BeforePath is null && LocalPath is null && GooglePath is null ? Path
        : $"Было: {BeforePath ?? "—"}\nФайл: {LocalPath ?? "—"}\nGoogle: {GooglePath ?? "—"}";
}

public static class PreviewDiff
{
    private readonly record struct Slot(bool Present, JsonNode? Node);
    private static readonly JsonSerializerOptions Display = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["familyName"] = "Фамилия", ["givenName"] = "Имя", ["middleName"] = "Отчество", ["unstructuredName"] = "Полное имя",
        ["displayName"] = "Отображаемое имя", ["displayNameLastFirst"] = "Имя для сортировки", ["value"] = "Значение",
        ["type"] = "Тип", ["name"] = "Название", ["key"] = "Ключ", ["streetAddress"] = "Улица", ["city"] = "Город",
        ["region"] = "Регион", ["country"] = "Страна", ["postalCode"] = "Индекс", ["title"] = "Должность",
        ["department"] = "Отдел", ["date"] = "Дата", ["year"] = "Год", ["month"] = "Месяц", ["day"] = "День"
    };
    public static IReadOnlyList<DiffRow> Build(SyncEntry entry, CancellationToken token = default)
    {
        var rows = new List<DiffRow>();
        if (entry.Local is JsonObject local && local["data"] is JsonObject && entry.Field is "$entity" or "$repairGoogleSnapshot")
        {
            foreach (var field in CapabilityRegistry.WritableFields)
                Walk("Ваши правки", CapabilityRegistry.Title(field), "$.data." + field, Child(ChildNode(entry.Before, "data"), field), Child(local["data"], field), Child(ChildNode(entry.Google, "data"), field), rows, token);
            foreach (var field in new[] { "labels", "starred", "photo" })
                Walk("Ваши правки", CapabilityRegistry.Title(field), "$." + field, Child(entry.Before, field), Child(entry.Local, field), Child(entry.Google, field), rows, token);
            Walk("Служебная копия Google", "Служебная копия", "$.google", Child(entry.Before, "google"), Child(entry.Local, "google"), Child(entry.Google, "google"), rows, token);
        }
        else
        {
            var section = entry.Field == "$snapshot" ? "Служебная копия Google" : "Изменения";
            Walk(section, CapabilityRegistry.Title(entry.Field), "$" + (entry.Field == "$snapshot" ? ".google" : (CapabilityRegistry.WritableFields.Contains(entry.Field) ? ".data." : ".") + entry.Field), new(entry.Before is not null, entry.Before), new(entry.Local is not null, entry.Local), new(entry.Google is not null, entry.Google), rows, token);
        }
        return rows;
    }
    private static JsonNode? ChildNode(JsonNode? parent, string key) => (parent as JsonObject)?[key];
    private static Slot Child(JsonNode? node, string key) => node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? new(true, value) : new(false, null);
    private static bool Equal(Slot a, Slot b) => a.Present == b.Present && JsonSemantics.Equal(a.Node, b.Node);
    private static string Format(Slot value) => !value.Present ? "Нет поля" : value.Node is null ? "null" : value.Node is JsonValue scalar && scalar.TryGetValue<string>(out var str) ? str.Length == 0 ? "\"\" (пустая строка)" : str : value.Node.ToJsonString(Display);
    private static string TypeLabel(Slot value) => value.Node is JsonObject ? "Объект" : value.Node is JsonArray a ? a.Count == 0 ? "[]" : $"Массив ({a.Count})" : Format(value);
    private static void Walk(string section, string title, string path, Slot before, Slot local, Slot google, List<DiffRow> rows, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Equal(before, local) && Equal(before, google)) return;
        var values = new[] { before, local, google };
        if (values.Any(v => v.Node is JsonObject) && values.All(v => !v.Present || v.Node is null or JsonObject))
        {
            var keys = values.SelectMany(v => (v.Node as JsonObject)?.Select(p => p.Key) ?? []).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (keys.Length > 0)
            {
                if (values.Any(v => v.Node is not JsonObject)) rows.Add(new(section, title + " · тип значения", path, TypeLabel(before), TypeLabel(local), TypeLabel(google)));
                foreach (var key in keys) Walk(section, Labels.GetValueOrDefault(key, key), path + "." + key, Child(before.Node, key), Child(local.Node, key), Child(google.Node, key), rows, token);
                return;
            }
        }
        if (values.All(v => !v.Present || v.Node is null or JsonArray) && values.Any(v => v.Node is JsonArray { Count: > 0 }))
        {
            var b = (before.Node as JsonArray)?.ToList() ?? []; var l = (local.Node as JsonArray)?.ToList() ?? []; var g = (google.Node as JsonArray)?.ToList() ?? [];
            if (values.Any(v => !v.Present || v.Node is null)) rows.Add(new(section, title + " · тип значения", path, TypeLabel(before), TypeLabel(local), TypeLabel(google)));
            // Singleton categories (e.g. names) have an unambiguous leaf comparison.
            if (new[] { "names", "birthdays", "biographies", "genders" }.Any(f => path.EndsWith("." + f, StringComparison.Ordinal)) && values.All(v => v.Node is not JsonArray { Count: > 1 }))
                Walk(section, title, path + "[0]", new(b.Count > 0, b.FirstOrDefault()), new(l.Count > 0, l.FirstOrDefault()), new(g.Count > 0, g.FirstOrDefault()), rows, token);
            else
            {
                // Match exact values only. Never guess which of several arbitrary phones was replaced.
                static Dictionary<string, Queue<(JsonNode? Node, int Index)>> Buckets(List<JsonNode?> items) => items.Select((node, index) => (Node: node, Index: index)).GroupBy(x => JsonSemantics.Canonical(x.Node)).ToDictionary(g => g.Key, g => new Queue<(JsonNode?, int)>(g));
                var lb = Buckets(l); var gb = Buckets(g);
                static int? Take(Dictionary<string, Queue<(JsonNode? Node, int Index)>> items, string key) => items.TryGetValue(key, out var queue) && queue.TryDequeue(out var item) ? item.Index : null;
                var index = 0;
                foreach (var item in b)
                {
                    token.ThrowIfCancellationRequested();
                    var key = JsonSemantics.Canonical(item); var localIndex = Take(lb, key); var googleIndex = Take(gb, key);
                    if (localIndex is null || googleIndex is null) rows.Add(new(section, title + " · прежнее значение", path + $"[{index}]", Format(new(true, item)), Format(new(localIndex is not null, item)), Format(new(googleIndex is not null, item)), path + $"[{index}]", localIndex is null ? null : path + $"[{localIndex}]", googleIndex is null ? null : path + $"[{googleIndex}]") { BeforeIsJson = item is JsonObject or JsonArray, LocalIsJson = localIndex is not null && item is JsonObject or JsonArray, GoogleIsJson = googleIndex is not null && item is JsonObject or JsonArray });
                    index++;
                }
                foreach (var (key, queue) in lb)
                    while (queue.TryDequeue(out var item))
                    {
                        token.ThrowIfCancellationRequested(); var googleIndex = Take(gb, key);
                        rows.Add(new(section, title + " · добавлено в файле", path + $"[{item.Index}]", "Нет значения", Format(new(true, item.Node)), Format(new(googleIndex is not null, item.Node)), LocalPath: path + $"[{item.Index}]", GooglePath: googleIndex is null ? null : path + $"[{googleIndex}]") { LocalIsJson = item.Node is JsonObject or JsonArray, GoogleIsJson = googleIndex is not null && item.Node is JsonObject or JsonArray });
                    }
                foreach (var queue in gb.Values)
                    while (queue.TryDequeue(out var item))
                    {
                        token.ThrowIfCancellationRequested(); rows.Add(new(section, title + " · добавлено в Google", path + $"[{item.Index}]", "Нет значения", "Нет значения", Format(new(true, item.Node)), GooglePath: path + $"[{item.Index}]") { GoogleIsJson = item.Node is JsonObject or JsonArray });
                    }
            }
            return;
        }
        rows.Add(new(section, title, path, Format(before), Format(local), Format(google)) { BeforeIsJson = before.Node is JsonObject or JsonArray, LocalIsJson = local.Node is JsonObject or JsonArray, GoogleIsJson = google.Node is JsonObject or JsonArray });
    }
}
