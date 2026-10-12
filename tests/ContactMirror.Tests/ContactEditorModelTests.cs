using System.Globalization;
using System.Text.Json.Nodes;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using Xunit;

namespace ContactMirror.Tests;

public sealed class ContactEditorModelTests
{
    [Fact]
    public void Editable_descriptors_cover_the_embedded_schema_with_actual_metadata_override()
    {
        using var stream = typeof(CapabilityRegistry).Assembly.GetManifestResourceStream("ContactMirror.Core.people-schema.json")!;
        var definitions = JsonNode.Parse(stream)!["schemas"]!.AsObject();
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in CapabilityRegistry.WritableFields)
            Inspect(definitions["Person"]!["properties"]![category]!.AsObject(), "data." + category, null);
        var fields = ContactFieldSchema.Fields;
        Assert.Equal(23, fields.Count);
        var actual = fields.SelectMany(Leaves).Select(field => field.Path).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Order(), actual.Order());
        Assert.Equal(actual.Order(), ContactTableQuery.Columns.Where(c => expected.Contains(c.Id)).Select(c => c.Id).Order());
        Assert.DoesNotContain(actual, path => path.EndsWith(".primary", StringComparison.Ordinal) || path.EndsWith(".verified", StringComparison.Ordinal) || path.Contains(".source.", StringComparison.Ordinal));
        Assert.Equal(23, actual.Count(path => path.EndsWith(".sourcePrimary", StringComparison.Ordinal)));
        Assert.All(fields.SelectMany(All), field => Assert.NotEqual(field.Name, field.Title));
        Assert.Equal(new[] { "biographies", "birthdays", "genders", "names" }, fields.Where(f => f.IsSingleton).Select(f => f.Name).Order());

        void Inspect(JsonObject schema, string path, string? parent)
        {
            if (schema["readOnly"]?.GetValue<bool>() == true || parent == "FieldMetadata" && !path.EndsWith(".sourcePrimary", StringComparison.Ordinal)) return;
            if (schema["$ref"] is JsonValue reference)
            {
                var name = reference.GetValue<string>();
                foreach (var child in definitions[name]!["properties"]!.AsObject()) Inspect(child.Value!.AsObject(), path + "." + child.Key, name);
            }
            else if (schema["type"]?.GetValue<string>() == "array") Inspect(schema["items"]!.AsObject(), path, null);
            else expected.Add(path);
        }
    }

    [Fact]
    public void Descriptor_choices_and_typed_leaves_do_not_loosen_actual_validator()
    {
        var biography = ContactFieldSchema.Fields.Single(f => f.Name == "biographies");
        var contentType = biography.Children.Single(f => f.Name == "contentType");
        Assert.Equal(new[] { "CONTENT_TYPE_UNSPECIFIED", "TEXT_PLAIN", "TEXT_HTML" }, contentType.Options.Select(o => o.Value));
        Assert.All(contentType.Options, option => Assert.NotEqual(option.Value, option.Title));
        var month = ContactFieldSchema.Fields.Single(f => f.Name == "birthdays").Children.Single(f => f.IsDate).Children.Single(f => f.Name == "month");
        Assert.Equal("integer", month.Type);
        Assert.Empty(CapabilityRegistry.ValidateEditable("birthdays", JsonNode.Parse("[{\"date\":{\"month\":2,\"day\":29},\"metadata\":{\"sourcePrimary\":true}}]")));
        Assert.NotEmpty(CapabilityRegistry.ValidateEditable("birthdays", JsonNode.Parse("[{\"date\":{\"year\":2023,\"month\":2,\"day\":29}}]")));
        Assert.NotEmpty(CapabilityRegistry.ValidateEditable("phoneNumbers", JsonNode.Parse("[{\"metadata\":{\"primary\":true}}]")));
        Assert.NotEmpty(CapabilityRegistry.ValidateEditable("phoneNumbers", JsonNode.Parse("[{\"metadata\":{\"source\":{\"id\":\"evil\"}}}]")));
    }

    [Fact]
    public void Leaf_columns_flatten_each_array_without_pairing_truncation_deduplication_or_type_changes()
    {
        var doc = Document("""
            {"phoneNumbers":[{"value":"+7 111","type":"work"},{"value":"+7 111","type":"home"},{"type":"mobile"}],
             "organizations":[{"startDate":{"year":2022,"month":5}},{"startDate":{"year":2024,"day":2}}],
             "names":null}
            """);
        var phones = ContactTableQuery.GetValues(doc, "data.phoneNumbers.value");
        Assert.Equal(new[] { "+7 111", "+7 111" }, phones.Select(v => v.GetValue<string>()));
        Assert.Equal(new[] { "work", "home", "mobile" }, ContactTableQuery.GetValues(doc, "data.phoneNumbers.type").Select(v => v.GetValue<string>()));
        Assert.Equal(new[] { 2022, 2024 }, ContactTableQuery.GetValues(doc, "data.organizations.startDate.year").Select(v => v.GetValue<int>()));
        Assert.Empty(ContactTableQuery.GetValues(doc, "data.names.givenName"));
        var dates = ContactTableQuery.GetValues(doc, "data.organizations.startDate");
        dates[0]["year"] = 1999;
        Assert.Equal(2022, doc["data"]!["organizations"]![0]!["startDate"]!["year"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("{}", false, true, false, true, false, true)]
    [InlineData("{\"phoneNumbers\":[]}", false, true, false, true, false, true)]
    [InlineData("{\"phoneNumbers\":null}", false, true, false, true, false, true)]
    [InlineData("{\"phoneNumbers\":[{\"value\":\"Alpha\"},{\"value\":\"Beta\"}]}", true, false, true, false, true, false)]
    [InlineData("{\"phoneNumbers\":[{\"value\":\"Beta\"}]}", true, false, false, true, false, true)]
    public void Array_filter_truth_table_is_any_positive_no_negative_and_explicit_missing(string data,
        bool has, bool missing, bool contains, bool notContains, bool equal, bool notEqual)
    {
        var document = Document(data);
        var operators = new[] { ContactFilterOperator.HasValue, ContactFilterOperator.Missing, ContactFilterOperator.Contains,
            ContactFilterOperator.NotContains, ContactFilterOperator.Equal, ContactFilterOperator.NotEqual };
        var expected = new[] { has, missing, contains, notContains, equal, notEqual };
        Assert.Equal(expected, operators.Select(op => ContactTableQuery.Matches(document, new("data.phoneNumbers.value", op, "alpha"))));
    }

    [Fact]
    public void Filters_combine_as_AND_OR_and_do_not_depend_on_visible_columns()
    {
        var a = Item(1, "{\"phoneNumbers\":[{\"value\":\"alpha\"}],\"emailAddresses\":[{\"value\":\"x@example.org\"}]}");
        var b = Item(2, "{\"phoneNumbers\":[{\"value\":\"alpha\"}]}");
        var c = Item(3, "{\"emailAddresses\":[{\"value\":\"x@example.org\"}]}");
        var rules = new[] { new ContactFilterRule("data.phoneNumbers.value", ContactFilterOperator.Equal, "alpha"), new ContactFilterRule("data.emailAddresses.value", ContactFilterOperator.HasValue) };
        var config = new ContactTableConfiguration([new("displayName")], [], rules);
        Assert.Equal(new[] { a.Id }, ContactTableQuery.Apply(Catalog(c, b, a), config).Select(i => i.Id));
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, ContactTableQuery.Apply(Catalog(c, b, a), config with { MatchAny = true }).Select(i => i.Id));
        Assert.Equal(2, config.Filters.Count);
    }

    [Fact]
    public void Sort_has_independent_numeric_date_boolean_and_array_minimum_oracles_with_missing_last()
    {
        var low = Item(1, "{\"organizations\":[{\"fullTimeEquivalentMillipercent\":2},{\"fullTimeEquivalentMillipercent\":100}]}" );
        var high = Item(2, "{\"organizations\":[{\"fullTimeEquivalentMillipercent\":10}]}" );
        var missing = Item(3, "{}");
        Assert.Equal(new[] { low.Id, high.Id, missing.Id }, Sorted(Catalog(missing, high, low), "data.organizations.fullTimeEquivalentMillipercent"));
        Assert.Equal(new[] { high.Id, low.Id, missing.Id }, Sorted(Catalog(missing, high, low), "data.organizations.fullTimeEquivalentMillipercent", true));
        var earlier = Item(4, "{\"events\":[{\"date\":{\"year\":2023,\"month\":12,\"day\":30}}]}" );
        var later = Item(5, "{\"events\":[{\"date\":{\"year\":2024,\"month\":1}}]}" );
        var partial = Item(6, "{\"events\":[{\"date\":{\"month\":2,\"day\":29}}]}" );
        Assert.Equal(new[] { later.Id, partial.Id, earlier.Id, missing.Id }, Sorted(Catalog(later, missing, earlier, partial), "data.events.date"));
        var blank = Item(7, "{\"phoneNumbers\":[{\"value\":\"\"}]}" );
        var valueWithBlank = Item(8, "{\"phoneNumbers\":[{\"value\":\"\"},{\"value\":\"A\"}]}" );
        Assert.Equal(new[] { valueWithBlank.Id, blank.Id }, Sorted(Catalog(blank, valueWithBlank), "data.phoneNumbers.value"));
        low.Document["starred"] = false; high.Document["starred"] = true;
        Assert.Equal(new[] { low.Id, high.Id }, Sorted(Catalog(high, low), "starred"));
    }

    [Fact]
    public void Multi_sort_matches_culture_oracle_then_UUID_and_minimum_does_not_reverse_with_descending()
    {
        var items = new[] { Item(9, "{\"names\":[{\"givenName\":\"Борис\"}],\"phoneNumbers\":[{\"value\":\"z\"},{\"value\":\"a\"}]}"),
            Item(7, "{\"names\":[{\"givenName\":\"анна\"}],\"phoneNumbers\":[{\"value\":\"c\"}]}"),
            Item(8, "{\"names\":[{\"givenName\":\"Анна\"}],\"phoneNumbers\":[{\"value\":\"b\"}]}"),
            Item(6, "{\"names\":[{\"givenName\":\"анна\"}],\"phoneNumbers\":[{\"value\":\"b\"}]}") };
        var expected = items.OrderBy(i => i.Document["data"]!["names"]![0]!["givenName"]!.GetValue<string>(),
            StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true)).ThenBy(i =>
                ((JsonArray)i.Document["data"]!["phoneNumbers"]!).Select(p => p!["value"]!.GetValue<string>()).Min(StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true)),
                StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true)).ThenBy(i => i.Id).Select(i => i.Id);
        var config = ContactTableConfiguration.Default with { Sorts = [new("displayName"), new("data.phoneNumbers.value")] };
        Assert.Equal(expected, ContactTableQuery.Apply(Catalog(items), config).Select(i => i.Id));
        Assert.Equal(new[] { items[1].Id, items[3].Id, items[2].Id, items[0].Id }, Sorted(Catalog(items), "data.phoneNumbers.value", true));
    }

    [Fact]
    public void Typed_comparison_filters_use_numbers_dates_and_booleans_and_reject_bad_operands()
    {
        var document = Document("{\"organizations\":[{\"fullTimeEquivalentMillipercent\":10}],\"events\":[{\"date\":{\"year\":2024,\"month\":1,\"day\":5}}]}");
        Assert.True(ContactTableQuery.Matches(document, new("data.organizations.fullTimeEquivalentMillipercent", ContactFilterOperator.Greater, "2")));
        Assert.False(ContactTableQuery.Matches(document, new("data.organizations.fullTimeEquivalentMillipercent", ContactFilterOperator.Greater, "bad")));
        Assert.True(ContactTableQuery.Matches(document, new("data.events.date", ContactFilterOperator.GreaterOrEqual, "2024-01-05")));
        Assert.False(ContactTableQuery.Matches(document, new("data.events.date", ContactFilterOperator.Less, "2024-01-05")));
        Assert.True(ContactTableQuery.Matches(document, new("starred", ContactFilterOperator.Equal, "false")));
        Assert.False(ContactTableQuery.IsSupported(ContactTableQuery.Columns.Single(c => c.Id == "data.phoneNumbers.value"), ContactFilterOperator.Greater));
    }

    [Fact]
    public void Search_uses_full_unicode_and_phone_values_and_label_names_independent_of_columns()
    {
        var document = Document("{\"phoneNumbers\":[{\"value\":\"+7 (999) 123-45-67\"}],\"biographies\":[{\"value\":\"Ёжик ищет Café в Москве\"}],\"names\":[{\"givenName\":\"Анна\"}]}" );
        var label = Guid.NewGuid(); document["labels"] = new JsonArray(label.ToString());
        var labels = new Dictionary<Guid, string> { [label] = "Друзья детства" };
        foreach (var text in new[] { "ёжик", "CAFÉ", "9991234567", "друзья ДЕТСТВА", "МОСКВЕ" }) Assert.True(ContactTableQuery.MatchesSearch(document, text, labels));
        Assert.False(ContactTableQuery.MatchesSearch(document, "Анна999", labels));
        Assert.True(ContactTableQuery.Matches(document, new("labels", ContactFilterOperator.Equal, label.ToString()), labels));
        Assert.True(ContactTableQuery.Matches(document, new("labels", ContactFilterOperator.Contains, "детства"), labels));
    }

    [Fact]
    public void Configuration_is_bounded_invalid_fields_do_not_become_columns_and_cancel_is_authoritative()
    {
        var config = new ContactTableConfiguration([new("invalid", 100), new("displayName", double.NaN), new("displayName", 200), new("starred", 2)],
            [new("google.etag"), new("displayName"), new("displayName", true)], [new("data.phoneNumbers.value", ContactFilterOperator.Greater, "5")]);
        var normalized = ContactTableQuery.Normalize(config);
        Assert.Equal(new[] { "displayName", "starred" }, normalized.Columns.Select(c => c.Id));
        Assert.Equal(new[] { 180d, 60d }, normalized.Columns.Select(c => c.Width));
        Assert.Single(normalized.Sorts); Assert.Empty(normalized.Filters);
        Assert.Equal(ContactTableConfiguration.Default.Columns, ContactTableQuery.Normalize(config with { Version = 999 }).Columns);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ContactTableQuery.Apply(Catalog(Item(1, "{}")), normalized, canceled.Token));
    }

    [Fact]
    public void Filter_operands_reject_invalid_calendar_dates_numbers_and_booleans_before_configuration()
    {
        var date = ContactTableQuery.Columns.Single(column => column.Id == "data.events.date");
        var number = ContactTableQuery.Columns.Single(column => column.Id == "data.organizations.fullTimeEquivalentMillipercent");
        var boolean = ContactTableQuery.Columns.Single(column => column.Id == "starred");
        foreach (var invalid in new[] { "2023-02-29", "2024-02-30", "2024-13-01", "not-a-date", "{\"month\":2,\"day\":30}" })
            Assert.False(ContactTableQuery.IsValidOperand(date, ContactFilterOperator.Equal, invalid));
        foreach (var valid in new[] { "2024-02-29", "--02-29", "0000-02-29", "{\"month\":2,\"day\":29}" })
            Assert.True(ContactTableQuery.IsValidOperand(date, ContactFilterOperator.Equal, valid));
        Assert.False(ContactTableQuery.IsValidOperand(number, ContactFilterOperator.Greater, "NaN"));
        Assert.False(ContactTableQuery.IsValidOperand(number, ContactFilterOperator.Greater, "wrong"));
        Assert.True(ContactTableQuery.IsValidOperand(number, ContactFilterOperator.Greater, "12,5"));
        Assert.False(ContactTableQuery.IsValidOperand(boolean, ContactFilterOperator.Equal, "wrong"));
        Assert.True(ContactTableQuery.IsValidOperand(boolean, ContactFilterOperator.Equal, "Да"));
        Assert.True(ContactTableQuery.IsValidOperand(date, ContactFilterOperator.Missing, ""));
        Assert.False(ContactTableQuery.IsValidOperand(boolean, ContactFilterOperator.Contains, "true"));
    }

    private static IEnumerable<ContactFieldDescriptor> All(ContactFieldDescriptor descriptor) => new[] { descriptor }.Concat(descriptor.Children.SelectMany(All));
    private static IEnumerable<ContactFieldDescriptor> Leaves(ContactFieldDescriptor descriptor) => descriptor.Children.Count == 0 ? new[] { descriptor } : descriptor.Children.SelectMany(Leaves);
    private static JsonObject Document(string data) => new() { ["schemaVersion"] = 1, ["id"] = Guid.NewGuid().ToString(), ["data"] = JsonNode.Parse(data), ["labels"] = new JsonArray(), ["starred"] = false };
    private static ContactCatalogItem Item(int index, string data)
    {
        var id = Guid.Parse($"00000000-0000-0000-0000-{index:D12}"); var document = Document(data); document["id"] = id.ToString(); return new(id, index + ".json", document);
    }
    private static ContactCatalog Catalog(params ContactCatalogItem[] items) => new("test", Guid.NewGuid(), items, new Dictionary<Guid, string>(), []);
    private static IEnumerable<Guid> Sorted(ContactCatalog catalog, string id, bool descending = false) => ContactTableQuery.Apply(catalog,
        ContactTableConfiguration.Default with { Sorts = [new(id, descending)] }).Select(item => item.Id);
}
