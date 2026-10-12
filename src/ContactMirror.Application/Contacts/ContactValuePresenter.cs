using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ContactMirror.Core;

namespace ContactMirror.Application.Contacts;

public enum ContactDateMode { Full, WithoutYear, YearOnly, YearAndMonth }

/// <summary>Read-only presentation and explicit input conversions. Raw query semantics remain separate.</summary>
public static class ContactValuePresenter
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly BigInteger MaxCoefficient = (BigInteger.One << 96) - 1;
    private const string PercentColumn = "data.organizations.fullTimeEquivalentMillipercent";

    public static string Format(JsonNode? node, string category, string? property = null)
    {
        if (node is null) return "";
        if (node is JsonArray array) return string.Join("; ", array.Select(item => Format(item, category, property)).Where(value => value.Length > 0));
        if (node is JsonObject obj)
        {
            if (property is "date" or "startDate" or "endDate" || property?.EndsWith(".date", StringComparison.Ordinal) == true) return FormatDate(obj);
            if (property is not null) return ObjectDetails(obj, category, property);
            var value = category switch
            {
                "names" => Text(obj, "unstructuredName") is { Length: > 0 } full ? full : Join(obj, " ", "givenName", "middleName", "familyName"),
                "phoneNumbers" or "emailAddresses" or "sipAddresses" or "urls" => Text(obj, "value"),
                "calendarUrls" => Text(obj, "url"),
                "imClients" => JoinPresent(obj, category, " · ", "username", "protocol"),
                "addresses" => Join(obj, ", ", "streetAddress", "extendedAddress", "poBox", "postalCode", "city", "region", "country"),
                "organizations" => Join(obj, " · ", "title", "name", "department"),
                "birthdays" or "events" => obj["date"] is null ? Text(obj, "text") : FormatDate(obj["date"]),
                "relations" => Text(obj, "person"),
                "clientData" or "userDefined" => Join(obj, ": ", "key", "value"),
                "biographies" => obj["contentType"]?.ToString() == "TEXT_HTML" ? "HTML (исходный текст)\n" + Text(obj, "value") : Text(obj, "value"),
                _ => Format(obj["value"], category, "value")
            };
            if (value.Length == 0) value = ObjectDetails(obj, category, null);
            var type = Format(obj["type"], category, "type");
            if (type.Length > 0) value = JoinNonempty(" · ", value, type);
            if (obj["metadata"]?["sourcePrimary"]?.GetValueKind() == JsonValueKind.True) value = JoinNonempty(" · ", value, "Основной");
            if (category == "organizations")
            {
                var period = JoinNonempty(" — ", Format(obj["startDate"], category, "startDate"), Format(obj["endDate"], category, "endDate"));
                value = JoinNonempty(" · ", value, period, obj["current"]?.GetValueKind() == JsonValueKind.True ? "Сейчас" : obj["current"]?.GetValueKind() == JsonValueKind.False ? "Ранее" : "");
            }
            return value;
        }
        if (property == "fullTimeEquivalentMillipercent" && TryExactDecimal(node.ToJsonString(), out var raw) && TryScale(raw, -3, out var percent)) return DecimalText(percent, Russian) + "%";
        if (node.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return node.GetValueKind() == JsonValueKind.True ? "Да" : "Нет";
        var text = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
        return property is null ? text : ContactPresentationCatalog.OptionTitle(category, property, text);
    }

    public static string FormatColumn(JsonObject document, string column, IReadOnlyDictionary<Guid, string>? labels = null)
    {
        if (column == "displayName") return ContactTableQuery.DisplayName(document);
        if (column == "organization") return PreferredSummary(document, "organizations");
        if (column == "photo") return document["photo"] is null ? "" : "Фото";
        if (column == "labels") return string.Join("; ", ContactTableQuery.GetValues(document, column).Select(value =>
            Guid.TryParse(value.ToString(), out var id) && labels?.TryGetValue(id, out var title) == true ? title : "Неизвестный ярлык (" + value + ")"));
        var parts = column.Split('.');
        var category = parts.Length > 1 && parts[0] == "data" ? parts[1] : "";
        var property = parts.Length > 2 ? string.Join('.', parts.Skip(2)) : null;
        return string.Join("; ", ContactTableQuery.GetValues(document, column).Select(value => Format(value, category, property)));
    }

    public static string ColumnTitle(ContactColumnDescriptor descriptor)
    {
        var parts = descriptor.Id.Split('.');
        if (parts.Length < 2 || parts[0] != "data") return descriptor.Title;
        return ContactPresentationCatalog.Title(parts[1], parts.Length > 2 ? string.Join('.', parts.Skip(2)) : null);
    }

    /// <summary>Original ordinal of the preferred local record; never joins Google or reorders the document.</summary>
    public static int? PreferredRecord(JsonObject document, string category)
    {
        if (document["data"]?[category] is not JsonArray rows) return null;
        var entries = rows.Select((row, index) => (row, index)).Where(entry => entry.row is JsonObject).ToArray();
        if (entries.Length == 0) return null;
        foreach (var entry in entries) if (entry.row?["metadata"]?["sourcePrimary"]?.GetValueKind() == JsonValueKind.True) return entry.index;
        if (category == "organizations") foreach (var entry in entries) if (entry.row?["current"]?.GetValueKind() == JsonValueKind.True) return entry.index;
        return entries[0].index;
    }
    public static string PreferredSummary(JsonObject document, string category) => PreferredRecord(document, category) is { } index
        ? Format(document["data"]![category]![index], category) : "";

    public static bool TryDateMode(JsonNode? node, out ContactDateMode mode)
    {
        mode = default;
        if (!DateParts(node, out var year, out var month, out var day) || !ValidDate(year, month, day)) return false;
        if (year > 0 && month > 0 && day > 0) mode = ContactDateMode.Full;
        else if (year == 0 && month > 0 && day > 0) mode = ContactDateMode.WithoutYear;
        else if (year > 0 && month == 0 && day == 0) mode = ContactDateMode.YearOnly;
        else if (year > 0 && month > 0 && day == 0) mode = ContactDateMode.YearAndMonth;
        else return false;
        return true;
    }

    public static string FormatDate(JsonNode? date)
    {
        if (date is null) return "";
        if (!TryDateMode(date, out var mode) || !DateParts(date, out var year, out var month, out var day)) return "Неполная дата: " + date.ToJsonString();
        return mode switch
        {
            ContactDateMode.Full => new DateTime(year, month, day).ToString("d MMMM yyyy", Russian),
            ContactDateMode.WithoutYear => new DateTime(2000, month, day).ToString("d MMMM", Russian),
            ContactDateMode.YearOnly => year.ToString("D4", CultureInfo.InvariantCulture),
            _ => Russian.DateTimeFormat.GetMonthName(month) + " " + year.ToString("D4", CultureInfo.InvariantCulture)
        };
    }

    public static bool TryCreateDate(ContactDateMode mode, int? year, int? month, int? day, out JsonObject? date, out string error)
    {
        date = null; error = "";
        var y = mode == ContactDateMode.WithoutYear ? 0 : year ?? 0;
        var m = mode == ContactDateMode.YearOnly ? 0 : month ?? 0;
        var d = mode is ContactDateMode.YearOnly or ContactDateMode.YearAndMonth ? 0 : day ?? 0;
        if (!Enum.IsDefined(mode) || !ValidDate(y, m, d) || mode != ContactDateMode.WithoutYear && y == 0
            || mode != ContactDateMode.YearOnly && m == 0 || mode is ContactDateMode.Full or ContactDateMode.WithoutYear && d == 0)
        { error = "Заполните выбранные части даты и проверьте число дней в месяце."; return false; }
        date = new JsonObject();
        if (mode != ContactDateMode.WithoutYear) date["year"] = y;
        if (mode != ContactDateMode.YearOnly) date["month"] = m;
        if (mode is ContactDateMode.Full or ContactDateMode.WithoutYear) date["day"] = d;
        return true;
    }
    public static string DateFilterOperand(JsonObject date) => DateParts(date, out var year, out var month, out var day) && ValidDate(year, month, day)
        ? year.ToString("D4", CultureInfo.InvariantCulture) + "-" + month.ToString("D2", CultureInfo.InvariantCulture) + "-" + day.ToString("D2", CultureInfo.InvariantCulture)
        : throw new ArgumentException("Дата содержит недопустимые компоненты.", nameof(date));

    public static string FormatFilterOperand(ContactFilterRule rule)
    {
        if (rule.Operator is ContactFilterOperator.HasValue or ContactFilterOperator.Missing) return "";
        if (rule.ColumnId == PercentColumn)
            return !FilterUsesRawUnits(rule) && TryExactDecimal(rule.Value, out var value) && TryScale(value, -3, out var percent) ? DecimalText(percent, Russian) + "%" : rule.Value;
        var descriptor = ContactTableQuery.Columns.FirstOrDefault(column => column.Id == rule.ColumnId);
        if (descriptor?.IsDate == true && TryParseDateOperand(rule.Value, out var date)) return FormatDate(date);
        if (descriptor?.Type == "boolean")
        {
            if (bool.TryParse(rule.Value, out var boolean)) return boolean ? "Да" : "Нет";
            if (rule.Value is "Да" or "да" or "1") return "Да";
            if (rule.Value is "Нет" or "нет" or "0") return "Нет";
        }
        var parts = rule.ColumnId.Split('.');
        if (parts.Length == 3 && rule.Operator is ContactFilterOperator.Equal or ContactFilterOperator.NotEqual)
            return ContactPresentationCatalog.OptionTitle(parts[1], parts[2], rule.Value);
        return rule.Value;
    }
    public static bool FilterUsesRawUnits(ContactFilterRule rule) => rule.ColumnId == PercentColumn
        && rule.Operator is not (ContactFilterOperator.HasValue or ContactFilterOperator.Missing)
        && (!TryExactDecimal(rule.Value, out var value) || !TryScale(value, -3, out _));
    public static string FilterTitle(ContactFilterRule rule) => FilterUsesRawUnits(rule) ? "Занятость, тысячные доли %"
        : ContactTableQuery.Columns.FirstOrDefault(column => column.Id == rule.ColumnId) is { } descriptor ? ColumnTitle(descriptor) : rule.ColumnId;

    public static bool TryParseFilterInput(ContactColumnDescriptor descriptor, string user, out string raw, out string error) =>
        TryParseFilterInput(descriptor, user, false, out raw, out error);
    public static bool TryParseFilterInput(ContactColumnDescriptor descriptor, string user, bool rawUnits, out string raw, out string error)
    {
        raw = user; error = "";
        if (descriptor.Id == PercentColumn && !rawUnits)
        {
            if (!TryExactDecimal(PercentInput(user), out var percent) || !TryScale(percent, 3, out var stored))
            { error = "Введите точный процент: значение выходит за пределы десятичного числа."; return false; }
            raw = DecimalText(stored, CultureInfo.InvariantCulture); return true;
        }
        if (descriptor.IsDate)
        {
            if (!TryParseDateOperand(user, out var date)) { error = "Проверьте дату: ГГГГ-ММ-ДД, неизвестная часть — 0."; return false; }
            raw = DateFilterOperand(date!); return true;
        }
        if (!ContactTableQuery.IsValidOperand(descriptor, ContactFilterOperator.Equal, raw))
        { error = "Значение не соответствует типу поля."; return false; }
        return true;
    }

    /// <summary>Edit storage is Int32 millipercent; filter operands intentionally permit fractional decimals.</summary>
    public static bool TryParsePercentInput(string user, out int raw, out string error)
    {
        raw = 0; error = "";
        if (!TryExactDecimal(PercentInput(user), out var percent) || !TryScale(percent, 3, out var stored)
            || stored != decimal.Truncate(stored) || stored < int.MinValue || stored > int.MaxValue)
        { error = "Введите процент с точностью до трёх знаков после запятой в пределах поля занятости."; return false; }
        raw = (int)stored; return true;
    }

    public static IEnumerable<string> SearchAliases(JsonObject document)
    {
        if (document["data"] is not JsonObject data) yield break;
        foreach (var category in ContactFieldSchema.Fields)
        {
            if (data[category.Name] is not JsonArray rows) continue;
            foreach (var row in rows.OfType<JsonObject>())
            {
                foreach (var property in category.Children)
                {
                    if (property.IsDate && row[property.Name] is { } date && TryDateMode(date, out _)) yield return FormatDate(date);
                    if (row[property.Name] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        var raw = value.GetValue<string>();
                        var translated = ContactPresentationCatalog.Options(category.Name, property.Name).FirstOrDefault(option => option.Value == raw);
                        if (translated is not null) yield return translated.Title;
                    }
                }
                if (category.Name == "organizations" && row["fullTimeEquivalentMillipercent"] is { } amount)
                    yield return Format(amount, category.Name, "fullTimeEquivalentMillipercent");
            }
        }
    }

    private static bool TryParseDateOperand(string raw, out JsonObject? date)
    {
        date = null; var text = raw.Trim();
        if (text.StartsWith("--", StringComparison.Ordinal)) text = "0-" + text[2..];
        var parts = text.Split('-');
        if (parts.Length == 3 && int.TryParse(parts[0], out var year) && int.TryParse(parts[1], out var month) && int.TryParse(parts[2], out var day) && ValidDate(year, month, day))
        { date = new JsonObject { ["year"] = year, ["month"] = month, ["day"] = day }; return true; }
        try
        {
            if (JsonNode.Parse(raw) is JsonObject obj && DateParts(obj, out year, out month, out day) && ValidDate(year, month, day)) { date = obj; return true; }
        }
        catch (JsonException) { }
        return false;
    }
    private static bool DateParts(JsonNode? node, out int year, out int month, out int day)
    {
        year = month = day = 0;
        if (node is not JsonObject date || !date.Any(pair => pair.Key is "year" or "month" or "day") || date.Any(pair => pair.Key is not ("year" or "month" or "day"))) return false;
        var numbers = new int[3]; var keys = new[] { "year", "month", "day" };
        for (var index = 0; index < keys.Length; index++)
            if (date.ContainsKey(keys[index]) && (date[keys[index]] is not JsonValue number || !number.TryGetValue<int>(out numbers[index]))) return false;
        year = numbers[0]; month = numbers[1]; day = numbers[2]; return true;
    }
    private static bool ValidDate(int year, int month, int day) => year is >= 0 and <= 9999 && month is >= 0 and <= 12 && day is >= 0 and <= 31
        && (month == 0 || day == 0 || day <= DateTime.DaysInMonth(year == 0 ? 2000 : year, month));

    private static string ObjectDetails(JsonObject obj, string category, string? parent) => string.Join(", ", obj
        .Where(pair => pair.Value is not null && pair.Key is not ("metadata" or "type"))
        .Where(pair => parent is not null || category != "organizations" || pair.Key is not ("startDate" or "endDate" or "current"))
        .Select(pair => ContactPresentationCatalog.Title(category, parent is null ? pair.Key : parent + "." + pair.Key) + ": " + Format(pair.Value, category, parent is null ? pair.Key : parent + "." + pair.Key)));
    private static string Text(JsonObject obj, string property) => obj[property]?.ToString() ?? "";
    private static string Join(JsonObject obj, string separator, params string[] keys) => string.Join(separator, keys.Select(key => Text(obj, key)).Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string JoinPresent(JsonObject obj, string category, string separator, params string[] keys) => string.Join(separator, keys.Select(key => Format(obj[key], category, key)).Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string JoinNonempty(string separator, params string[] values) => string.Join(separator, values.Where(value => value.Length > 0));
    private static string PercentInput(string text) { var value = text.Trim(); return value.EndsWith('%') ? value[..^1].Trim() : value; }
    private static string DecimalText(decimal value, CultureInfo culture) => value.ToString("0.############################", culture);

    // decimal.TryParse may round excess precision. Parse coefficient and scale first and reject inexact values.
    private static bool TryExactDecimal(string text, out decimal result)
    {
        result = 0;
        Match match;
        try { match = Regex.Match(text.Trim().Replace(',', '.'), @"^([+-]?)([0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE]([+-]?[0-9]+))?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
        if (!match.Success) return false;
        var mantissa = match.Groups[2].Value; var point = mantissa.IndexOf('.');
        var scale = point < 0 ? 0 : mantissa.Length - point - 1;
        if (match.Groups[3].Success)
        {
            if (!int.TryParse(match.Groups[3].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent) || exponent is < -1000 or > 1000) return false;
            scale -= exponent;
        }
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return true;
        while (scale > 0 && digits.EndsWith('0')) { digits = digits[..^1]; scale--; }
        if (scale > 28 || digits.Length + Math.Max(0, -scale) > 29) return false;
        var coefficient = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        if (scale < 0) { coefficient *= BigInteger.Pow(10, -scale); scale = 0; }
        return TryDecimal(coefficient, scale, match.Groups[1].Value == "-", out result);
    }
    private static bool TryScale(decimal value, int exponent, out decimal result)
    {
        var bits = decimal.GetBits(value); var negative = (bits[3] & int.MinValue) != 0; var scale = ((bits[3] >> 16) & 0xff) - exponent;
        var coefficient = new BigInteger((uint)bits[0]) + (new BigInteger((uint)bits[1]) << 32) + (new BigInteger((uint)bits[2]) << 64);
        if (coefficient.IsZero) { result = 0; return true; }
        while (scale > 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        if (scale < 0) { coefficient *= BigInteger.Pow(10, -scale); scale = 0; }
        return TryDecimal(coefficient, scale, negative, out result);
    }
    private static bool TryDecimal(BigInteger coefficient, int scale, bool negative, out decimal result)
    {
        result = 0;
        if (coefficient > MaxCoefficient || scale is < 0 or > 28) return false;
        result = new decimal(unchecked((int)(uint)(coefficient & uint.MaxValue)), unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue)),
            unchecked((int)(uint)((coefficient >> 64) & uint.MaxValue)), negative, (byte)scale);
        return true;
    }
}
