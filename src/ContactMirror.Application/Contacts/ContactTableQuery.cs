using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Core;

namespace ContactMirror.Application.Contacts;

public sealed record ContactColumnDescriptor(string Id, string Title, string Type, bool IsDate = false);
public sealed record ContactColumnSetting(string Id, double Width = 180);
public sealed record ContactSortRule(string ColumnId, bool Descending = false);
public enum ContactFilterOperator { HasValue, Missing, Contains, NotContains, Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual }
public sealed record ContactFilterRule(string ColumnId, ContactFilterOperator Operator, string Value = "");
public sealed record ContactTableConfiguration(IReadOnlyList<ContactColumnSetting> Columns,
    IReadOnlyList<ContactSortRule> Sorts, IReadOnlyList<ContactFilterRule> Filters,
    bool MatchAny = false, string Search = "", int Version = 1)
{
    public static ContactTableConfiguration Default => new(
        [new("photo", 80), new("displayName", 230), new("data.phoneNumbers.value", 190), new("data.emailAddresses.value", 220),
         new("organization", 220), new("labels", 160), new("starred", 90)], [new("displayName")], []);
}

/// <summary>Pure local table projection; filters and sort never alter a contact or truncate stored values.</summary>
public static class ContactTableQuery
{
    private static readonly CompareInfo Russian = CultureInfo.GetCultureInfo("ru-RU").CompareInfo;
    public static IReadOnlyList<ContactColumnDescriptor> Columns { get; } = BuildColumns();
    private static readonly IReadOnlyDictionary<string, ContactColumnDescriptor> ById = Columns.ToDictionary(c => c.Id, StringComparer.Ordinal);

    private static IReadOnlyList<ContactColumnDescriptor> BuildColumns()
    {
        var result = new List<ContactColumnDescriptor>
        {
            new("photo", "Фотография", "boolean"), new("displayName", "Имя", "string"),
            new("organization", "Организация и должность", "string"), new("labels", "Ярлыки", "string"),
            new("starred", "Избранное", "boolean"), new("id", "Идентификатор контакта", "string")
        };
        foreach (var field in ContactFieldSchema.Fields) Add(field, field.Title);
        return result.AsReadOnly();
        void Add(ContactFieldDescriptor field, string title)
        {
            if (field.Children.Count == 0 || field.Path.Count(c => c == '.') == 1 || field.IsDate)
                result.Add(new(field.Path, title, field.Type, field.IsDate));
            foreach (var child in field.Children) Add(child, title + " · " + child.Title);
        }
    }

    public static ContactTableConfiguration Normalize(ContactTableConfiguration? configuration)
    {
        if (configuration is null || configuration.Version != 1) return ContactTableConfiguration.Default;
        var columns = (configuration.Columns ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id) && ById.ContainsKey(c.Id))
            .DistinctBy(c => c.Id).Take(Columns.Count).Select(c => c with { Width = double.IsFinite(c.Width) ? Math.Clamp(c.Width, 60, 1200) : 180 }).ToArray();
        var sorts = (configuration.Sorts ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.ColumnId) && ById.ContainsKey(s.ColumnId))
            .DistinctBy(s => s.ColumnId).Take(Columns.Count).ToArray();
        var filters = (configuration.Filters ?? []).Where(f => f is not null && !string.IsNullOrWhiteSpace(f.ColumnId) && ById.TryGetValue(f.ColumnId, out var field)
            && Enum.IsDefined(f.Operator) && IsSupported(field, f.Operator))
            .Take(100).Select(f => f with { Value = (f.Value ?? "")[..Math.Min((f.Value ?? "").Length, 10000)] }).ToArray();
        return new(columns.Length == 0 ? ContactTableConfiguration.Default.Columns : columns,
            sorts, filters, configuration.MatchAny, (configuration.Search ?? "")[..Math.Min((configuration.Search ?? "").Length, 10000)]);
    }

    public static bool IsSupported(ContactColumnDescriptor column, ContactFilterOperator operation) => operation switch
    {
        ContactFilterOperator.Greater or ContactFilterOperator.GreaterOrEqual or ContactFilterOperator.Less or ContactFilterOperator.LessOrEqual =>
            column.IsDate || column.Type is "integer" or "number",
        ContactFilterOperator.Contains or ContactFilterOperator.NotContains => column.Type is "string" or "array" or "object",
        _ => true
    };

    public static bool IsValidOperand(ContactColumnDescriptor column, ContactFilterOperator operation, string value)
    {
        if (!Enum.IsDefined(operation) || !IsSupported(column, operation)) return false;
        return operation is ContactFilterOperator.HasValue or ContactFilterOperator.Missing or ContactFilterOperator.Contains or ContactFilterOperator.NotContains
            || ParseOperand(value ?? "", column) is not null;
    }

    public static IReadOnlyList<ContactCatalogItem> Apply(ContactCatalog catalog, ContactTableConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = Normalize(configuration);
        var result = new List<ContactCatalogItem>();
        var sortKeys = new Dictionary<Guid, JsonNode?[]>();
        foreach (var contact in catalog.Contacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesSearch(contact.Document, config.Search, catalog.Labels)) continue;
            if (config.Filters.Count != 0 && (config.MatchAny
                    ? !config.Filters.Any(rule => Matches(contact.Document, rule, catalog.Labels))
                    : !config.Filters.All(rule => Matches(contact.Document, rule, catalog.Labels)))) continue;
            result.Add(contact);
            sortKeys[contact.Id] = config.Sorts.Select(s => Minimum(GetSortValues(contact.Document, s.ColumnId, catalog.Labels))).ToArray();
        }
        try { result.Sort((left, right) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < config.Sorts.Count; index++)
            {
                var a = sortKeys[left.Id][index]; var b = sortKeys[right.Id][index];
                // Missing is last in either direction; the minimum of all values is the row key.
                if (a is null && b is not null) return 1;
                if (a is not null && b is null) return -1;
                var compared = CompareValues(a, b);
                if (compared != 0) return config.Sorts[index].Descending ? -compared : compared;
            }
            return left.Id.CompareTo(right.Id);
        }); }
        catch (InvalidOperationException error) when (error.InnerException is OperationCanceledException)
        { throw new OperationCanceledException(cancellationToken); }
        return result.AsReadOnly();
    }

    /// <summary>Array traversal retains every value, including duplicates. Null/missing and empty arrays have no values.</summary>
    public static IReadOnlyList<JsonNode> GetValues(JsonObject document, string columnPath)
    {
        if (columnPath == "displayName") return [JsonValue.Create(DisplayName(document))!];
        if (columnPath == "photo") return document["photo"] is null ? [] : [JsonValue.Create(true)!];
        if (columnPath == "starred") return [JsonValue.Create(document["starred"]?.GetValueKind() == JsonValueKind.True)!];
        if (columnPath == "organization")
            return GetValues(document, "data.organizations.name").Concat(GetValues(document, "data.organizations.title")).ToArray();
        var parts = columnPath.Replace("[]", "", StringComparison.Ordinal).Split('.');
        var result = new List<JsonNode>();
        Visit(document, 0);
        return result.AsReadOnly();
        void Visit(JsonNode? node, int position)
        {
            if (node is null) return;
            if (node is JsonArray array) { foreach (var item in array) Visit(item, position); return; }
            if (position == parts.Length) { result.Add(node.DeepClone()); return; }
            if (node is JsonObject obj) Visit(obj[parts[position]], position + 1);
        }
    }

    public static string DisplayName(JsonObject document)
    {
        var names = document["data"]?["names"] as JsonArray;
        if (names is null) return "Без имени";
        foreach (var name in names.OfType<JsonObject>())
        {
            var full = name["unstructuredName"]?.ToString();
            if (!string.IsNullOrWhiteSpace(full)) return full;
            full = string.Join(" ", new[] { "givenName", "middleName", "familyName" }.Select(key => name[key]?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)));
            if (full.Length != 0) return full;
        }
        return "Без имени";
    }

    public static string FormatValues(JsonObject document, string columnId, IReadOnlyDictionary<Guid, string>? labels = null) =>
        string.Join("; ", GetSortValues(document, columnId, labels).Select(FormatValue));

    public static string FormatValue(JsonNode value)
    {
        if (TryDate(value, out var date)) return $"{(date.Year == 0 ? "0000" : date.Year.ToString("D4", CultureInfo.InvariantCulture))}-{date.Month:D2}-{date.Day:D2}";
        if (value is JsonObject obj) return string.Join(", ", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Where(pair => pair.Value is not null).Select(pair => ContactFieldSchema.FieldTitle(pair.Key) + ": " + FormatValue(pair.Value!)));
        if (value is JsonArray array) return string.Join("; ", array.Where(item => item is not null).Select(item => FormatValue(item!)));
        if (value.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return value.GetValueKind() == JsonValueKind.True ? "Да" : "Нет";
        return ScalarText(value);
    }

    public static bool Matches(JsonObject document, ContactFilterRule rule, IReadOnlyDictionary<Guid, string>? labels = null)
    {
        if (!ById.TryGetValue(rule.ColumnId, out var descriptor) || !IsSupported(descriptor, rule.Operator)) return false;
        var values = GetValues(document, rule.ColumnId);
        if (rule.Operator == ContactFilterOperator.HasValue) return values.Count != 0;
        if (rule.Operator == ContactFilterOperator.Missing) return values.Count == 0;
        var operand = ParseOperand(rule.Value ?? "", descriptor);
        if (operand is null && rule.Operator is not (ContactFilterOperator.Contains or ContactFilterOperator.NotContains)) return false;
        var negative = rule.Operator is ContactFilterOperator.NotContains or ContactFilterOperator.NotEqual;
        var any = values.Any(value =>
        {
            if (rule.Operator is ContactFilterOperator.Contains or ContactFilterOperator.NotContains)
            {
                var text = rule.ColumnId == "labels" && Guid.TryParse(value.ToString(), out var id) && labels?.TryGetValue(id, out var title) == true ? title : FormatValue(value);
                return Contains(text, rule.Value ?? "");
            }
            if (operand is null) return false;
            var compared = CompareValues(value, operand);
            return rule.Operator switch
            {
                ContactFilterOperator.Equal or ContactFilterOperator.NotEqual => compared == 0,
                ContactFilterOperator.Greater => compared > 0, ContactFilterOperator.GreaterOrEqual => compared >= 0,
                ContactFilterOperator.Less => compared < 0, ContactFilterOperator.LessOrEqual => compared <= 0, _ => false
            };
        });
        return negative ? !any : any;
    }

    public static bool MatchesSearch(JsonObject document, string search, IReadOnlyDictionary<Guid, string>? labels = null)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var query = search.Trim().Normalize(NormalizationForm.FormC);
        var text = string.Join("\n", Scalars(document["data"]).Concat(Scalars(document["extensions"]))
            .Select(ScalarText).Concat(GetSortValues(document, "labels", labels).Select(ScalarText)).Append(DisplayName(document)).Append(document["id"]?.ToString() ?? ""));
        if (Contains(text, query)) return true;
        var digits = Digits(query);
        if (digits.Length == 0 || query.Any(char.IsLetter)) return false;
        return GetValues(document, "data.phoneNumbers.value").Any(value => Digits(value.ToString()).Contains(digits, StringComparison.Ordinal));
    }

    private static IReadOnlyList<JsonNode> GetSortValues(JsonObject document, string id, IReadOnlyDictionary<Guid, string>? labels)
    {
        var values = GetValues(document, id);
        if (id != "labels" || labels is null) return values;
        return values.Select(value => Guid.TryParse(value.ToString(), out var labelId) && labels.TryGetValue(labelId, out var title)
            ? (JsonNode)JsonValue.Create(title)! : value).ToArray();
    }
    private static JsonNode? Minimum(IReadOnlyList<JsonNode> values)
    {
        JsonNode? minimum = null;
        foreach (var value in values)
        {
            if (value.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetValue<string>())) continue;
            if (value is JsonObject { Count: 0 } or JsonArray { Count: 0 }) continue;
            if (minimum is null || CompareValues(value, minimum) < 0) minimum = value;
        }
        return minimum;
    }
    private static int CompareValues(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null ? right is null ? 0 : 1 : -1;
        if (TryDate(left, out var a) && TryDate(right, out var b)) return (a.Month, a.Day, a.Year).CompareTo((b.Month, b.Day, b.Year));
        if (left is JsonValue l && right is JsonValue r)
        {
            if (left.GetValueKind() == JsonValueKind.Number && right.GetValueKind() == JsonValueKind.Number
                && decimal.TryParse(left.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ln)
                && decimal.TryParse(right.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rn)) return ln.CompareTo(rn);
            if (l.TryGetValue<bool>(out var lb) && r.TryGetValue<bool>(out var rb)) return lb.CompareTo(rb);
        }
        return Russian.Compare(FormatValue(left), FormatValue(right), CompareOptions.IgnoreCase);
    }
    private static JsonNode? ParseOperand(string text, ContactColumnDescriptor descriptor)
    {
        if (descriptor.IsDate)
        {
            var normalizedDate = text.Trim();
            if (normalizedDate.StartsWith("--", StringComparison.Ordinal)) normalizedDate = "0-" + normalizedDate[2..];
            var parts = normalizedDate.Split('-');
            if (parts.Length == 3 && int.TryParse(parts[0], out var year) && int.TryParse(parts[1], out var month) && int.TryParse(parts[2], out var day)
                && ValidDate(year, month, day))
                return new JsonObject { ["year"] = year, ["month"] = month, ["day"] = day };
            try { if (JsonNode.Parse(text) is JsonObject date && TryDate(date, out var parsed) && ValidDate(parsed.Year, parsed.Month, parsed.Day)) return date; } catch (JsonException) { }
            return null;
        }
        if (descriptor.Type is "integer" or "number")
            return decimal.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : null;
        if (descriptor.Type == "boolean")
            return bool.TryParse(text, out var boolean) ? JsonValue.Create(boolean) : text is "Да" or "да" or "1" ? JsonValue.Create(true) : text is "Нет" or "нет" or "0" ? JsonValue.Create(false) : null;
        return JsonValue.Create(text);
    }
    private static bool TryDate(JsonNode node, out (int Year, int Month, int Day) value)
    {
        value = default;
        if (node is not JsonObject obj || !obj.Any(p => p.Key is "year" or "month" or "day") || obj.Any(p => p.Key is not ("year" or "month" or "day"))) return false;
        var parts = new int[3]; var names = new[] { "year", "month", "day" };
        for (var i = 0; i < names.Length; i++)
        {
            if (obj[names[i]] is null) continue;
            if (obj[names[i]] is not JsonValue scalar || !scalar.TryGetValue<int>(out parts[i])) return false;
        }
        value = (parts[0], parts[1], parts[2]); return true;
    }
    private static bool ValidDate(int year, int month, int day) => year is >= 0 and <= 9999 && month is >= 0 and <= 12 && day is >= 0 and <= 31
        && (month == 0 || day == 0 || day <= DateTime.DaysInMonth(year == 0 ? 2000 : year, month));
    private static IEnumerable<JsonNode> Scalars(JsonNode? node)
    {
        if (node is JsonObject obj) { foreach (var pair in obj) foreach (var value in Scalars(pair.Value)) yield return value; }
        else if (node is JsonArray array) { foreach (var item in array) foreach (var value in Scalars(item)) yield return value; }
        else if (node is not null) yield return node;
    }
    private static string ScalarText(JsonNode node) => node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    private static bool Contains(string text, string query) => Russian.IndexOf(text.Normalize(NormalizationForm.FormC), query.Normalize(NormalizationForm.FormC), CompareOptions.IgnoreCase) >= 0;
    private static string Digits(string text) => string.Concat(text.Where(char.IsDigit));
}
