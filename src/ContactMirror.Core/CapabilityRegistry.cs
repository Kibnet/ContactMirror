using System.Text.Json.Nodes;

namespace ContactMirror.Core;

public sealed record FieldCapability(string Name, string Title, string Mode);

public static class CapabilityRegistry
{
    public static readonly string[] WritableFields = ["addresses", "biographies", "birthdays", "calendarUrls", "clientData", "emailAddresses", "events", "externalIds", "genders", "imClients", "interests", "locales", "locations", "miscKeywords", "names", "nicknames", "occupations", "organizations", "phoneNumbers", "relations", "sipAddresses", "urls", "userDefined"];
    public static readonly string[] ReadFields = [.. WritableFields, "memberships", "metadata", "ageRanges", "coverPhotos", "photos", "skills"];
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["addresses"] = "Адреса", ["biographies"] = "Заметки", ["birthdays"] = "День рождения", ["calendarUrls"] = "Календари", ["clientData"] = "Данные приложений",
        ["emailAddresses"] = "Email", ["events"] = "События", ["externalIds"] = "Внешние идентификаторы", ["genders"] = "Пол и обращения", ["imClients"] = "Мессенджеры",
        ["interests"] = "Интересы", ["locales"] = "Языки", ["locations"] = "Местоположения", ["miscKeywords"] = "Ключевые слова", ["names"] = "Имя",
        ["nicknames"] = "Псевдонимы", ["occupations"] = "Занятия", ["organizations"] = "Организации", ["phoneNumbers"] = "Телефоны", ["relations"] = "Связи",
        ["sipAddresses"] = "SIP-адреса", ["urls"] = "Ссылки", ["userDefined"] = "Пользовательские поля", ["labels"] = "Ярлыки", ["starred"] = "Избранное", ["photo"] = "Фотография", ["$snapshot"] = "Сведения из Google"
    };
    public static string Title(string name) => name.StartsWith("$restore:", StringComparison.Ordinal) ? "Восстановление: " + Title(name[9..]) : name.StartsWith("$journal:", StringComparison.Ordinal) ? "Сверка: " + Title(name[9..]) : Titles.GetValueOrDefault(name, name switch { "$entity" => "Контакт", "$group" => "Ярлык", "$restoreCreate" => "Восстановить запись", "$completeAbsent" => "Завершить удаление", _ => name });
    public static IReadOnlyList<FieldCapability> Fields { get; } = WritableFields.Select(n => new FieldCapability(n, Title(n), "В обе стороны"))
        .Concat(new[] { new FieldCapability("memberships", "Ярлыки и избранное", "В обе стороны"), new FieldCapability("photos", "Фотографии", "В обе стороны, отдельный метод"), new FieldCapability("ageRanges", "Возрастная категория", "Только чтение"), new FieldCapability("coverPhotos", "Обложки профиля", "Только чтение"), new FieldCapability("metadata", "Источники и метаданные", "Только чтение"), new FieldCapability("skills", "Навыки", "Только чтение"), new FieldCapability("fileAses", "Имя для сортировки", "Нет в документированных масках API") }).ToArray();
    private static readonly JsonObject Schemas;
    static CapabilityRegistry()
    {
        using var stream = typeof(CapabilityRegistry).Assembly.GetManifestResourceStream("ContactMirror.Core.people-schema.json")!;
        Schemas = JsonNode.Parse(stream)!["schemas"]!.AsObject();
    }

    public static JsonNode? WritableProjection(string field, JsonNode? value) => value is null ? null : Project(value, Schemas["Person"]!["properties"]![field]!.AsObject(), field);

    private static JsonNode? Project(JsonNode? value, JsonObject schema, string path)
    {
        if (value is null) return null;
        if (schema["readOnly"]?.GetValue<bool>() == true) return null;
        if (schema["$ref"] is JsonValue reference)
        {
            var name = reference.GetValue<string>();
            var definition = Schemas[name]!.AsObject();
            if (value is not JsonObject obj) return value.DeepClone();
            var result = new JsonObject();
            foreach (var pair in obj)
            {
                // Field source is descriptive; a source identifier must never be uploaded from an editable field.
                if (name == "FieldMetadata" && pair.Key != "sourcePrimary") continue;
                var childSchema = definition["properties"]?[pair.Key] as JsonObject;
                if (childSchema is null || childSchema["readOnly"]?.GetValue<bool>() == true) continue;
                var child = Project(pair.Value, childSchema, path + "." + pair.Key);
                if (child is JsonObject { Count: 0 }) continue;
                result[pair.Key] = child;
            }
            return result;
        }
        if (schema["type"]?.GetValue<string>() == "array" && value is JsonArray a)
            return new JsonArray(a.Select(x => Project(x, schema["items"]!.AsObject(), path + "[]")).ToArray());
        return value.DeepClone();
    }

    public static IReadOnlyList<string> ValidateEditable(string field, JsonNode? value)
    {
        var errors = new List<string>();
        if (!WritableFields.Contains(field, StringComparer.Ordinal)) return [$"Поле {field} не поддерживается для записи."];
        Validate(value, Schemas["Person"]!["properties"]![field]!.AsObject(), field, errors, editable: true);
        if (value is JsonArray arr && field is "names" or "birthdays" or "biographies" or "genders" && arr.Count > 1)
            errors.Add($"{Title(field)}: Google допускает одну запись.");
        if (value is JsonArray fields && fields.Count(x => x is JsonObject item && item["metadata"] is JsonObject metadata && metadata["sourcePrimary"] is JsonValue primary && primary.TryGetValue<bool>(out var selected) && selected) > 1)
            errors.Add($"{Title(field)}: основная запись может быть только одна.");
        return errors;
    }

    public static IReadOnlyList<string> UnknownReplacementFields(string field, JsonNode? value)
    {
        var errors = new List<string>();
        if (value is not null) Validate(value, Schemas["Person"]!["properties"]![field]!.AsObject(), field, errors, editable: false);
        return errors;
    }

    private static void Validate(JsonNode? value, JsonObject schema, string path, List<string> errors, bool editable)
    {
        if (schema["readOnly"]?.GetValue<bool>() == true)
        {
            if (editable) errors.Add($"{path}: поле доступно только для чтения.");
            return;
        }
        if (value is null) { if (editable) errors.Add($"{path}: null недопустим; для очистки используйте []."); return; }
        if (schema["$ref"] is JsonValue reference)
        {
            var name = reference.GetValue<string>();
            if (value is not JsonObject obj) { errors.Add($"{path}: ожидается объект."); return; }
            foreach (var pair in obj)
            {
                var next = Schemas[name]!["properties"]?[pair.Key] as JsonObject;
                if (next is null) { errors.Add($"{path}.{pair.Key}: неизвестное поле; заменять эту категорию пока нельзя."); continue; }
                if (name == "FieldMetadata" && pair.Key != "sourcePrimary")
                {
                    if (editable) errors.Add($"{path}.{pair.Key}: метаданные источника доступны только для чтения.");
                    continue;
                }
                Validate(pair.Value, next, path + "." + pair.Key, errors, editable);
            }
            if (name == "Date" && editable)
            {
                int Number(string key) => obj[key] is JsonValue v && v.TryGetValue<int>(out var number) ? number : 0;
                if (Number("month") is < 0 or > 12 || Number("day") is < 0 or > 31 || Number("year") is < 0 or > 9999) errors.Add($"{path}: неверная дата.");
                else if (Number("month") > 0 && Number("day") > 0 && Number("day") > DateTime.DaysInMonth(Number("year") == 0 ? 2000 : Number("year"), Number("month"))) errors.Add($"{path}: несуществующий день месяца.");
            }
            return;
        }
        var type = schema["type"]?.GetValue<string>();
        if (type == "array")
        {
            if (value is not JsonArray arr) { errors.Add($"{path}: ожидается массив."); return; }
            foreach (var item in arr) Validate(item, schema["items"]!.AsObject(), path + "[]", errors, editable);
            return;
        }
        var kind = value.GetValueKind();
        var valid = type switch { "string" => kind == System.Text.Json.JsonValueKind.String, "integer" => kind == System.Text.Json.JsonValueKind.Number && value.AsValue().TryGetValue<int>(out _), "boolean" => kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False, "number" => kind == System.Text.Json.JsonValueKind.Number, _ => true };
        if (!valid) errors.Add($"{path}: неверный тип значения ({type}).");
        else if (schema["enum"] is JsonArray allowed && !allowed.Any(x => JsonSemantics.Equal(x, value))) errors.Add($"{path}: неподдерживаемое значение перечисления.");
    }
}
