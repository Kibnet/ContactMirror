using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using Xunit;

namespace ContactMirror.Tests;

public sealed class ContactPresentationTests
{
    [Fact]
    public void Catalog_covers_all_111_leaves_and_every_documented_choice_without_changing_validation()
    {
        var definitions = Schema(); var leaves = new List<(string Category, string Property, JsonObject Schema)>();
        foreach (var category in CapabilityRegistry.WritableFields) Inspect(definitions["Person"]!["properties"]![category]!.AsObject(), category, "", null);
        Assert.Equal(111, leaves.Count);
        foreach (var leaf in leaves)
        {
            var title = ContactPresentationCatalog.Title(leaf.Category, leaf.Property);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.NotEqual(leaf.Property.Split('.').Last(), title);
            var description = leaf.Schema["description"]?.GetValue<string>() ?? "";
            var expected = leaf.Schema["enum"] is JsonArray enumeration ? enumeration.Select(value => value!.GetValue<string>()).ToArray()
                : description.Contains("predefined values", StringComparison.Ordinal)
                    ? Regex.Matches(description, @"\*\s*`([^`]+)`").Select(match => match.Groups[1].Value).ToArray() : [];
            var choices = ContactPresentationCatalog.Options(leaf.Category, leaf.Property);
            Assert.Equal(expected, choices.Select(choice => choice.Value));
            if (expected.Length == 0) continue;
            Assert.Equal(leaf.Schema["enum"] is null, ContactPresentationCatalog.AllowsCustom(leaf.Category, leaf.Property));
            Assert.All(choices, choice => Assert.False(string.IsNullOrWhiteSpace(choice.Title)));
            foreach (var choice in choices)
            {
                var row = new JsonObject { [leaf.Property] = choice.Value };
                Assert.Empty(CapabilityRegistry.ValidateEditable(leaf.Category, new JsonArray(row)));
            }
            var custom = new JsonArray(new JsonObject { [leaf.Property] = "Custom: Точное ЗНАЧЕНИЕ" });
            Assert.Equal(leaf.Schema["enum"] is null, CapabilityRegistry.ValidateEditable(leaf.Category, custom).Count == 0);
        }
        Assert.Equal(144, ContactTableQuery.Columns.Count);
        Assert.Equal("Тип телефона", ContactTableQuery.Columns.Single(column => column.Id == "data.phoneNumbers.type").Title);
        Assert.Equal("Номер телефона", ContactTableQuery.Columns.Single(column => column.Id == "data.phoneNumbers.value").Title);
        Assert.Equal("Адрес электронной почты", ContactPresentationCatalog.Title("emailAddresses", "value"));
        Assert.Equal("Личный", ContactPresentationCatalog.OptionTitle("emailAddresses", "type", "home"));
        Assert.Equal("Домашний", ContactPresentationCatalog.OptionTitle("phoneNumbers", "type", "home"));
        Assert.Equal("Работа", ContactPresentationCatalog.OptionTitle("organizations", "type", "work"));
        Assert.Empty(ContactPresentationCatalog.Options("genders", "addressMeAs"));
        Assert.False(ContactPresentationCatalog.AllowsCustom("genders", "addressMeAs"));
        Assert.Equal("He/Him", ContactValuePresenter.Format(JsonValue.Create("He/Him"), "genders", "addressMeAs"));

        void Inspect(JsonObject schema, string category, string property, string? parent)
        {
            if (schema["readOnly"]?.GetValue<bool>() == true || parent == "FieldMetadata" && property.Split('.').Last() != "sourcePrimary") return;
            if (schema["$ref"] is JsonValue reference)
                foreach (var child in definitions[reference.GetValue<string>()]!["properties"]!.AsObject())
                    Inspect(child.Value!.AsObject(), category, property.Length == 0 ? child.Key : property + "." + child.Key, reference.GetValue<string>());
            else if (schema["type"]?.GetValue<string>() == "array") Inspect(schema["items"]!.AsObject(), category, property, null);
            else leaves.Add((category, property, schema));
        }
    }

    [Fact]
    public void Display_is_readonly_preserves_custom_case_duplicates_raw_order_null_and_HTML_source()
    {
        var document = Document("""
            {"phoneNumbers":[{"value":"Second","type":"HOME","metadata":{"sourcePrimary":false}},
              {"value":"First","type":"workMobile","metadata":{"sourcePrimary":true}},
              {"value":"Second","type":"Custom MiXeD"},{"type":null},{"type":""}],
             "biographies":[{"value":"<script>alert('fixture')</script>&amp;","contentType":"TEXT_HTML"}],
             "organizations":[{"name":"Original","current":false},{"name":"Current","current":true},
              {"name":"Preferred","metadata":{"sourcePrimary":true}}]}
            """);
        document["google"] = JsonNode.Parse("""{"person":{"phoneNumbers":[{"value":"REMOTE","metadata":{"primary":true}}],"organizations":[{"name":"REMOTE"}]}}""");
        var before = document.ToJsonString();
        Assert.Equal("HOME", ContactValuePresenter.Format(JsonValue.Create("HOME"), "phoneNumbers", "type"));
        Assert.Equal("Custom MiXeD", ContactValuePresenter.Format(JsonValue.Create("Custom MiXeD"), "phoneNumbers", "type"));
        Assert.Equal("", ContactValuePresenter.Format(null, "phoneNumbers", "type"));
        Assert.Equal("", ContactValuePresenter.Format(JsonValue.Create(""), "phoneNumbers", "type"));
        Assert.Equal("Second; First; Second", ContactValuePresenter.FormatColumn(document, "data.phoneNumbers.value"));
        Assert.Equal(1, ContactValuePresenter.PreferredRecord(document, "phoneNumbers"));
        Assert.Equal(2, ContactValuePresenter.PreferredRecord(document, "organizations"));
        Assert.Equal("First · Рабочий мобильный · Основной", ContactValuePresenter.PreferredSummary(document, "phoneNumbers"));
        Assert.DoesNotContain("REMOTE", ContactValuePresenter.FormatColumn(document, "organization"));
        Assert.Equal("HTML (исходный текст)\n<script>alert('fixture')</script>&amp;", ContactValuePresenter.FormatColumn(document, "data.biographies"));
        foreach (var column in ContactTableQuery.Columns) _ = ContactValuePresenter.FormatColumn(document, column.Id);
        _ = ContactValuePresenter.SearchAliases(document).ToArray();
        Assert.Equal(before, document.ToJsonString());
        var withoutPrimary = Document("""{"organizations":[{"name":"Original"},{"name":"Current","current":true}],"phoneNumbers":[{"value":"Original"},{"value":"Other","type":"main"}]}""");
        Assert.Equal(1, ContactValuePresenter.PreferredRecord(withoutPrimary, "organizations"));
        Assert.Equal(0, ContactValuePresenter.PreferredRecord(withoutPrimary, "phoneNumbers"));
    }

    [Theory]
    [InlineData(ContactDateMode.Full, 1990, 10, 12, "12 октября 1990", "1990-10-12")]
    [InlineData(ContactDateMode.WithoutYear, 0, 2, 29, "29 февраля", "0000-02-29")]
    [InlineData(ContactDateMode.YearOnly, 1990, 0, 0, "1990", "1990-00-00")]
    [InlineData(ContactDateMode.YearAndMonth, 1990, 10, 0, "октябрь 1990", "1990-10-00")]
    public void Date_modes_roundtrip_without_an_invented_year(ContactDateMode mode, int year, int month, int day, string title, string filter)
    {
        Assert.True(ContactValuePresenter.TryCreateDate(mode, year == 0 ? null : year, month == 0 ? null : month, day == 0 ? null : day, out var date, out var error), error);
        Assert.Equal(title, ContactValuePresenter.FormatDate(date));
        Assert.True(ContactValuePresenter.TryDateMode(date, out var restored)); Assert.Equal(mode, restored);
        Assert.Equal(filter, ContactValuePresenter.DateFilterOperand(date!));
        var descriptor = ContactTableQuery.Columns.Single(column => column.Id == "data.birthdays.date");
        Assert.True(ContactValuePresenter.TryParseFilterInput(descriptor, filter, out var raw, out error), error); Assert.Equal(filter, raw);
        if (mode == ContactDateMode.WithoutYear) Assert.False(date!.ContainsKey("year"));
    }

    [Fact]
    public void Existing_partial_invalid_and_legacy_dates_are_never_clamped_or_rewritten()
    {
        foreach (var raw in new[] { "{\"year\":2023,\"month\":2,\"day\":29}", "{\"day\":12}", "{\"month\":10}", "{\"year\":null,\"month\":2,\"day\":29}" })
        {
            var date = JsonNode.Parse(raw)!; Assert.StartsWith("Неполная дата: ", ContactValuePresenter.FormatDate(date)); Assert.Equal(raw, date.ToJsonString());
        }
        Assert.False(ContactValuePresenter.TryCreateDate(ContactDateMode.Full, 2023, 2, 29, out _, out _));
        Assert.False(ContactValuePresenter.TryCreateDate(ContactDateMode.Full, null, null, null, out _, out _));
        foreach (var raw in new[] { "0000-02-29", "--02-29", "{\"month\":2,\"day\":29}" })
        {
            var rule = new ContactFilterRule("data.birthdays.date", ContactFilterOperator.Equal, raw);
            Assert.Equal("29 февраля", ContactValuePresenter.FormatFilterOperand(rule)); Assert.Equal(raw, rule.Value);
        }
        var birthday = JsonNode.Parse("{\"text\":\"Летом, год неизвестен\"}")!;
        Assert.Equal("Летом, год неизвестен", ContactValuePresenter.Format(birthday, "birthdays"));
    }

    [Theory]
    [InlineData("12500", "12,5%")]
    [InlineData("100000", "100%")]
    [InlineData("0", "0%")]
    [InlineData("-12345.6789", "-12,3456789%")]
    [InlineData("1000e-28", "0,0000000000000000000000000001%")]
    [InlineData("-12.345678901234567890123456", "-0,012345678901234567890123456%")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950,335%")]
    public void Filter_percent_scaling_is_exact_and_preserves_raw_operand(string original, string expected)
    {
        var rule = PercentRule(original); Assert.False(ContactValuePresenter.FilterUsesRawUnits(rule));
        Assert.Equal(expected, ContactValuePresenter.FormatFilterOperand(rule)); Assert.Equal(original, rule.Value);
        Assert.True(ContactValuePresenter.TryParseFilterInput(PercentDescriptor(), expected, out var raw, out var error), error);
        Assert.Equal(decimal.Parse(original, NumberStyles.Float, CultureInfo.InvariantCulture), decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1e-28")]
    [InlineData("-1e-28")]
    [InlineData("1.2345678901234567890123456789")]
    public void Unrepresentable_percent_display_uses_explicit_exact_raw_fallback(string raw)
    {
        var rule = PercentRule(raw);
        Assert.True(ContactValuePresenter.FilterUsesRawUnits(rule)); Assert.Equal(raw, ContactValuePresenter.FormatFilterOperand(rule));
        Assert.Equal("Занятость, тысячные доли %", ContactValuePresenter.FilterTitle(rule)); Assert.Equal(raw, rule.Value);
        Assert.True(ContactValuePresenter.TryParseFilterInput(PercentDescriptor(), raw, true, out var reopened, out var error), error); Assert.Equal(raw, reopened);
    }

    [Fact]
    public void Explicit_percent_edits_are_Int32_and_filter_conversion_allows_fractional_raw_without_rounding()
    {
        foreach (var raw in new[] { int.MinValue, -12345, 0, 12500, 100000, int.MaxValue })
        {
            var expectedPercent = (raw / 1000m).ToString(CultureInfo.InvariantCulture);
            Assert.True(ContactValuePresenter.TryParsePercentInput(expectedPercent + "%", out var actual, out var error), error); Assert.Equal(raw, actual);
            Assert.Equal((raw / 1000m).ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")) + "%", ContactValuePresenter.Format(JsonValue.Create(raw), "organizations", "fullTimeEquivalentMillipercent"));
        }
        Assert.True(ContactValuePresenter.TryParseFilterInput(PercentDescriptor(), "12,3456%", out var operand, out _)); Assert.Equal("12345.6", operand);
        Assert.False(ContactValuePresenter.TryParsePercentInput("12,3456%", out _, out _));
        foreach (var invalid in new[] { "oops", "12%%", "79228162514264337593543950335%", "1e-29%", "1e1000%", "0.12345678901234567890123456789%" })
            Assert.False(ContactValuePresenter.TryParseFilterInput(PercentDescriptor(), invalid, out _, out _));
        var other = ContactTableQuery.Columns.Single(column => column.Id == "data.organizations.startDate.year");
        Assert.True(ContactValuePresenter.TryParseFilterInput(other, "12.5", out operand, out _)); Assert.Equal("12.5", operand);
        Assert.Equal("", ContactValuePresenter.FormatFilterOperand(new(PercentDescriptor().Id, ContactFilterOperator.HasValue, "12500")));
    }

    [Fact]
    public void Friendly_aliases_extend_search_but_typed_predicates_and_sorting_keep_raw_units_and_tokens()
    {
        var document = Document("""{"phoneNumbers":[{"value":"+7 999 123","type":"workMobile"}],"birthdays":[{"date":{"month":2,"day":29}}],"organizations":[{"fullTimeEquivalentMillipercent":12500}]}""");
        Assert.True(ContactTableQuery.MatchesSearch(document, "workMobile")); Assert.True(ContactTableQuery.MatchesSearch(document, "Рабочий мобильный"));
        Assert.True(ContactTableQuery.MatchesSearch(document, "29 февраля")); Assert.True(ContactTableQuery.MatchesSearch(document, "7999123"));
        Assert.True(ContactTableQuery.Matches(document, new("data.phoneNumbers.type", ContactFilterOperator.Equal, "workMobile")));
        Assert.False(ContactTableQuery.Matches(document, new("data.phoneNumbers.type", ContactFilterOperator.Equal, "Рабочий мобильный")));
        Assert.False(ContactTableQuery.Matches(document, new("data.phoneNumbers.type", ContactFilterOperator.Contains, "мобильный")));
        Assert.True(ContactTableQuery.Matches(document, new("data.phoneNumbers.type", ContactFilterOperator.Contains, "Mobile")));
        Assert.True(ContactTableQuery.Matches(document, PercentRule("12500"))); Assert.False(ContactTableQuery.Matches(document, PercentRule("12.5")));
        var second = Document("""{"organizations":[{"fullTimeEquivalentMillipercent":100000}],"birthdays":[{"date":{"year":1990,"month":1,"day":1}}]}""");
        var id1 = Guid.Parse(document["id"]!.GetValue<string>()); var id2 = Guid.Parse(second["id"]!.GetValue<string>());
        var catalog = new ContactCatalog("synthetic", Guid.NewGuid(), [new(id1, "one", document), new(id2, "two", second)], new Dictionary<Guid, string>(), []);
        Assert.Equal(new[] { id1, id2 }, ContactTableQuery.Apply(catalog, ContactTableConfiguration.Default with { Sorts = [new(PercentDescriptor().Id)] }).Select(row => row.Id));
        Assert.Equal(new[] { id2, id1 }, ContactTableQuery.Apply(catalog, ContactTableConfiguration.Default with { Sorts = [new("data.birthdays.date")] }).Select(row => row.Id));
    }

    private static JsonObject Schema() { using var stream = typeof(CapabilityRegistry).Assembly.GetManifestResourceStream("ContactMirror.Core.people-schema.json")!; return JsonNode.Parse(stream)!["schemas"]!.AsObject(); }
    private static JsonObject Document(string data) => new() { ["schemaVersion"] = 1, ["id"] = Guid.NewGuid().ToString(), ["data"] = JsonNode.Parse(data), ["labels"] = new JsonArray(), ["starred"] = false, ["extensions"] = new JsonObject() };
    private static ContactColumnDescriptor PercentDescriptor() => ContactTableQuery.Columns.Single(column => column.Id == "data.organizations.fullTimeEquivalentMillipercent");
    private static ContactFilterRule PercentRule(string raw) => new("data.organizations.fullTimeEquivalentMillipercent", ContactFilterOperator.Equal, raw);
}
