using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContactMirror.Core;

public static class DocumentCodec
{
    public static ContactDocument Contact(JsonObject doc)
    {
        ValidateHeader(doc);
        ValidateKeys(doc, ["schemaVersion", "id", "data", "labels", "starred", "photo", "extensions", "google"]);
        if (doc["data"] is not JsonObject) throw new SyncException("invalidDocument", "В контакте должен быть объект data.");
        if (doc.ContainsKey("extensions") && doc["extensions"] is not JsonObject) throw new SyncException("invalidDocument", "extensions должен быть объектом.");
        if (doc.ContainsKey("labels") && doc["labels"] is not JsonArray) throw new SyncException("invalidDocument", "labels должен быть массивом UUID.");
        ContactDocument contact;
        try { contact = doc.Deserialize<ContactDocument>(JsonSemantics.Options) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new SyncException("invalidDocument", "Неверный тип поля в файле контакта.", inner: ex); }
        if (contact.Labels.Count != contact.Labels.Distinct().Count()) throw new SyncException("invalidLabels", "Ярлыки контакта содержат повторяющиеся идентификаторы.");
        foreach (var field in contact.Data)
        {
            var errors = CapabilityRegistry.ValidateEditable(field.Key, field.Value);
            if (errors.Count > 0) throw new SyncException("invalidField", string.Join("\n", errors));
        }
        return contact;
    }
    public static GroupDocument Group(JsonObject doc)
    {
        ValidateHeader(doc);
        ValidateKeys(doc, ["schemaVersion", "id", "name", "clientData", "extensions", "google"]);
        if (doc.ContainsKey("extensions") && doc["extensions"] is not JsonObject) throw new SyncException("invalidDocument", "extensions должен быть объектом.");
        if (doc.ContainsKey("clientData") && doc["clientData"] is not JsonArray) throw new SyncException("invalidDocument", "clientData должен быть массивом.");
        GroupDocument group;
        try { group = doc.Deserialize<GroupDocument>(JsonSemantics.Options) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new SyncException("invalidDocument", "Неверный тип поля в файле ярлыка.", inner: ex); }
        if (string.IsNullOrWhiteSpace(group.Name)) throw new SyncException("invalidGroup", "Имя ярлыка не может быть пустым.");
        foreach (var item in group.ClientData)
            if (item is not JsonObject o || o["key"]?.GetValueKind() != JsonValueKind.String || o["value"]?.GetValueKind() != JsonValueKind.String || o.Any(p => p.Key is not ("key" or "value")))
                throw new SyncException("invalidGroup", "clientData ярлыка должен содержать пары key/value.");
        return group;
    }
    public static JsonObject ToJson<T>(T doc) => JsonSerializer.SerializeToNode(doc, JsonSemantics.Options)!.AsObject();
    private static void ValidateHeader(JsonObject doc)
    {
        if (doc["schemaVersion"] is not JsonValue version || !version.TryGetValue<int>(out var v) || v != 1) throw new SyncException("schemaVersion", "Версия формата не поддерживается. Файл сохранён без изменений.");
        if (!Guid.TryParse(doc["id"]?.ToString(), out var id) || id == Guid.Empty) throw new SyncException("invalidId", "У файла нет корректного id (UUID). Создайте шаблон контакта в приложении.");
    }
    private static void ValidateKeys(JsonObject doc, string[] keys)
    {
        var unknown = doc.Select(x => x.Key).Where(k => !keys.Contains(k, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0) throw new SyncException("unknownEnvelope", "Неизвестные свойства документа: " + string.Join(", ", unknown) + ". Локальные данные можно хранить в extensions.");
    }
}
