using System.Text.Json.Nodes;

namespace ContactMirror.Core;

public static class PersonCodec
{
    public static string Resource(JsonObject person) => person["resourceName"]?.GetValue<string>() ?? throw new SyncException("missingResource", "Google вернул контакт без идентификатора.");
    public static JsonObject Source(JsonObject person)
    {
        var contacts = (person["metadata"]?["sources"] as JsonArray)?.OfType<JsonObject>().Where(s => s["type"]?.GetValue<string>() == "CONTACT").ToArray() ?? [];
        if (contacts.Length != 1 || string.IsNullOrWhiteSpace(contacts[0]["id"]?.GetValue<string>())) throw new SyncException("ambiguousSource", "Google вернул несколько источников контакта или источник не определён. Автоматическая запись остановлена.");
        return contacts[0];
    }
    public static string SourceId(JsonObject person) => Source(person)["id"]!.GetValue<string>();
    public static bool IsContactValue(JsonNode? value, JsonObject person)
    {
        var source = value?["metadata"]?["source"];
        if (source is not null) return source["type"]?.GetValue<string>() == "CONTACT" && source["id"]?.GetValue<string>() == SourceId(person);
        var sources = person["metadata"]?["sources"] as JsonArray;
        return sources?.Count == 1 && sources[0]?["type"]?.GetValue<string>() == "CONTACT";
    }
    public static JsonArray ContactValues(JsonObject person, string field) => new((person[field] as JsonArray)?.Where(x => IsContactValue(x, person)).Select(JsonSemantics.Clone).ToArray() ?? []);
    public static JsonObject Data(JsonObject person)
    {
        var result = new JsonObject();
        foreach (var field in CapabilityRegistry.WritableFields)
        {
            var values = ContactValues(person, field);
            if (values.Count > 0) result[field] = CapabilityRegistry.WritableProjection(field, values);
        }
        return result;
    }
    public static JsonObject Snapshot(JsonObject person) => new() { ["resourceName"] = Resource(person), ["person"] = person.DeepClone() };
    public static string? PhotoUrl(JsonObject person) => (person["photos"] as JsonArray)?.FirstOrDefault(x => IsContactValue(x, person) && x?["default"]?.GetValue<bool>() != true)?["url"]?.GetValue<string>();
    public static IReadOnlyList<string> Memberships(JsonObject person) => (person["memberships"] as JsonArray)?.Where(x => IsContactValue(x, person)).Select(x => x?["contactGroupMembership"]?["contactGroupResourceName"]?.GetValue<string>()).Where(x => x is not null).Cast<string>().Distinct().ToArray() ?? [];
    public static string DisplayName(ContactDocument doc)
    {
        var name = (doc.Data["names"] as JsonArray)?.FirstOrDefault();
        var assembled = string.Join(" ", new[] { name?["givenName"]?.GetValue<string>(), name?["middleName"]?.GetValue<string>(), name?["familyName"]?.GetValue<string>() }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return assembled.Length > 0 ? assembled : name?["unstructuredName"]?.GetValue<string>() ?? (doc.Google?["person"]?["names"] as JsonArray)?.FirstOrDefault()?["displayName"]?.GetValue<string>() ?? (doc.Data["emailAddresses"] as JsonArray)?.FirstOrDefault()?["value"]?.GetValue<string>() ?? "Контакт без имени";
    }
}
