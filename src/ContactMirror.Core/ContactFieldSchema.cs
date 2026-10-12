using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace ContactMirror.Core;

public sealed record ContactFieldOption(string Value, string Title)
{
    public override string ToString() => Title;
}
public sealed record ContactFieldDescriptor(string Name, string Title, string Type,
    IReadOnlyList<ContactFieldDescriptor> Children, IReadOnlyList<ContactFieldOption> Options,
    bool IsSingleton, string Path, bool IsDate = false);

/// <summary>Immutable editable descriptors from the same embedded People schema used by validation.</summary>
public static class ContactFieldSchema
{
    public static IReadOnlyList<ContactFieldDescriptor> Fields { get; } = Load();

    private static IReadOnlyList<ContactFieldDescriptor> Load()
    {
        using var stream = typeof(CapabilityRegistry).Assembly.GetManifestResourceStream("ContactMirror.Core.people-schema.json")!;
        var definitions = JsonNode.Parse(stream)!["schemas"]!.AsObject();
        var properties = definitions["Person"]!["properties"]!.AsObject();
        var fields = new List<ContactFieldDescriptor>();
        foreach (var category in CapabilityRegistry.WritableFields)
            fields.Add(Build(category, properties[category]!.AsObject(), "data." + category, definitions,
                category is "names" or "birthdays" or "biographies" or "genders")!);
        return fields.AsReadOnly();
    }

    private static ContactFieldDescriptor? Build(string name, JsonObject schema, string path,
        JsonObject definitions, bool singleton = false, string? parentReference = null)
    {
        if (schema["readOnly"]?.GetValue<bool>() == true || parentReference == "FieldMetadata" && name != "sourcePrimary") return null;
        var type = schema["type"]?.GetValue<string>() ?? "object";
        var reference = schema["$ref"]?.GetValue<string>();
        var children = new List<ContactFieldDescriptor>();
        if (type == "array")
        {
            var item = schema["items"]!.AsObject();
            reference = item["$ref"]?.GetValue<string>();
            if (reference is not null) AddChildren(definitions[reference]!["properties"]!.AsObject(), reference);
        }
        else if (reference is not null) AddChildren(definitions[reference]!["properties"]!.AsObject(), reference);
        else if (schema["properties"] is JsonObject childProperties) AddChildren(childProperties, null);
        var options = new List<ContactFieldOption>();
        if (schema["enum"] is JsonArray values)
            foreach (var value in values) options.Add(new(value!.GetValue<string>(), OptionTitle(value.GetValue<string>())));
        return new(name, path.Count(c => c == '.') == 1 ? CapabilityRegistry.Title(name) : FieldTitle(name),
            type, children.AsReadOnly(), options.AsReadOnly(), singleton, path, reference == "Date");

        void AddChildren(JsonObject childProperties, string? childReference)
        {
            foreach (var property in childProperties)
            {
                var child = Build(property.Key, property.Value!.AsObject(), path + "." + property.Key, definitions, parentReference: childReference);
                if (child is not null) children.Add(child);
            }
        }
    }

    public static string FieldTitle(string name) => name switch
    {
        "value" => "Значение", "type" => "Тип", "metadata" => "Основная запись", "sourcePrimary" => "Основное",
        "year" => "Год", "month" => "Месяц", "day" => "День", "date" => "Дата", "text" => "Текст даты",
        "city" => "Город", "formattedValue" => "Полный адрес", "region" => "Регион", "postalCode" => "Почтовый индекс",
        "poBox" => "Абонентский ящик", "country" => "Страна", "streetAddress" => "Улица и дом", "countryCode" => "Код страны",
        "extendedAddress" => "Дополнение к адресу", "contentType" => "Формат заметки", "url" => "Ссылка", "key" => "Название поля",
        "displayName" => "Отображаемое имя", "addressMeAs" => "Обращение", "protocol" => "Сервис", "username" => "Имя пользователя",
        "current" => "Текущее", "floor" => "Этаж", "deskCode" => "Рабочее место", "floorSection" => "Секция этажа", "buildingId" => "Здание",
        "honorificPrefix" => "Обращение перед именем", "honorificSuffix" => "Дополнение после имени", "familyName" => "Фамилия",
        "givenName" => "Имя", "middleName" => "Отчество", "unstructuredName" => "Полное имя",
        "phoneticMiddleName" => "Произношение отчества", "phoneticFullName" => "Произношение полного имени",
        "phoneticHonorificPrefix" => "Произношение обращения", "phoneticGivenName" => "Произношение имени",
        "phoneticHonorificSuffix" => "Произношение дополнения", "phoneticFamilyName" => "Произношение фамилии",
        "endDate" => "Дата окончания", "startDate" => "Дата начала", "jobDescription" => "Обязанности", "symbol" => "Биржевой символ",
        "title" => "Должность", "fullTimeEquivalentMillipercent" => "Занятость (тысячные процента)", "name" => "Название",
        "costCenter" => "Центр затрат", "phoneticName" => "Произношение названия", "department" => "Подразделение",
        "location" => "Расположение", "domain" => "Домен", "person" => "Человек", _ => name
    };

    private static string OptionTitle(string value) => value switch
    {
        "CONTENT_TYPE_UNSPECIFIED" or "TYPE_UNSPECIFIED" => "Не задано", "TEXT_PLAIN" => "Обычный текст", "TEXT_HTML" => "HTML",
        "HOME" => "Домашний", "WORK" => "Рабочий", "OTHER" => "Другой", "DEFAULT" => "Обычный", "MAIDEN_NAME" => "Девичья фамилия",
        "INITIALS" => "Инициалы", "GPLUS" => "Имя Google+", "OTHER_NAME" => "Другое имя", "ALTERNATE_NAME" => "Альтернативное имя",
        "SHORT_NAME" => "Короткое имя", "OUTLOOK_BILLING_INFORMATION" => "Сведения об оплате Outlook",
        "OUTLOOK_DIRECTORY_SERVER" => "Сервер каталога Outlook", "OUTLOOK_KEYWORD" => "Ключевое слово Outlook",
        "OUTLOOK_MILEAGE" => "Пробег Outlook", "OUTLOOK_PRIORITY" => "Приоритет Outlook", "OUTLOOK_SENSITIVITY" => "Конфиденциальность Outlook",
        "OUTLOOK_SUBJECT" => "Тема Outlook", "OUTLOOK_USER" => "Пользователь Outlook", _ => value
    };
}
