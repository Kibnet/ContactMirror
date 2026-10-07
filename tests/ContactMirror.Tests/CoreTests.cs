using System.Text.Json.Nodes;
using ContactMirror.Core;
using Xunit;

namespace ContactMirror.Tests;

public class CoreTests
{
    [Theory]
    [InlineData("a", "a", "a", null)]
    [InlineData("a", "b", "a", ChangeKind.Upload)]
    [InlineData("a", "a", "c", ChangeKind.Download)]
    [InlineData("a", "b", "b", ChangeKind.Reconcile)]
    [InlineData("a", "b", "c", ChangeKind.Conflict)]
    public void ThreeWayPreservesBothChanges(string b, string l, string r, ChangeKind? kind) => Assert.Equal(kind, ThreeWayPlanner.Compare(JsonValue.Create(b), JsonValue.Create(l), JsonValue.Create(r)));

    [Fact] public void GeneratedThreeWayPhoneArraysPreserveMultiplicityAndDirection()
    {
        var random = new Random(601006);
        int[] Values() => Enumerable.Range(0, random.Next(6)).Select(_ => random.Next(5)).ToArray();
        JsonArray Array(int[] values) => new(values.OrderBy(_ => random.Next()).Select(value => (JsonNode)new JsonObject { ["value"] = "+7 " + value, ["type"] = "Рабочий" }).ToArray());
        string Key(int[] values) => string.Join(",", values.Order());
        for (var index = 0; index < 512; index++)
        {
            var b = Values(); var l = index % 3 == 0 ? b : Values();
            var r = index % 4 == 0 ? b : index % 4 == 1 ? l : Values();
            var (bk, lk, rk) = (Key(b), Key(l), Key(r));
            ChangeKind? expected = lk == rk ? lk == bk ? null : ChangeKind.Reconcile : lk == bk ? ChangeKind.Download : rk == bk ? ChangeKind.Upload : ChangeKind.Conflict;
            Assert.Equal(expected, ThreeWayPlanner.Compare(Array(b), Array(l), Array(r)));
            var reversed = expected switch { ChangeKind.Upload => ChangeKind.Download, ChangeKind.Download => ChangeKind.Upload, _ => expected };
            Assert.Equal(reversed, ThreeWayPlanner.Compare(Array(b), Array(r), Array(l)));
        }
    }

    [Fact] public void CanonicalArraysIgnoreOrderButKeepDuplicates()
    {
        Assert.True(JsonSemantics.Equal(JsonNode.Parse("[1,2,1]"), JsonNode.Parse("[2,1,1]")));
        Assert.False(JsonSemantics.Equal(JsonNode.Parse("[1,2,1]"), JsonNode.Parse("[1,2]")));
    }
    [Fact] public void DuplicateJsonPropertiesAreRejected() => Assert.Throws<SyncException>(() => JsonSemantics.ParseObject("{\"id\":1,\"id\":2}"u8.ToArray(), "contact.json"));
    [Fact] public void UnknownSiblingMakesReplacementUnsafe()
    {
        var raw = JsonNode.Parse("[{\"value\":\"+7 001\",\"newField\":\"valuable\",\"canonicalForm\":\"+7001\"}]");
        Assert.NotEmpty(CapabilityRegistry.UnknownReplacementFields("phoneNumbers", raw));
        Assert.Empty(CapabilityRegistry.UnknownReplacementFields("phoneNumbers", JsonNode.Parse("[{\"value\":\"+7 001\",\"canonicalForm\":\"+7001\"}]")));
    }
    [Fact] public void AllWritableCategoriesHaveSchema()
    {
        Assert.Equal(23, CapabilityRegistry.WritableFields.Length);
        foreach (var field in CapabilityRegistry.WritableFields) Assert.Empty(CapabilityRegistry.ValidateEditable(field, new JsonArray()));
    }
    [Fact] public void PartialBirthdayAndCustomLabelsSurvive()
    {
        var birthday = JsonNode.Parse("[{\"date\":{\"year\":0,\"month\":2,\"day\":29}}]");
        Assert.Empty(CapabilityRegistry.ValidateEditable("birthdays", birthday));
        Assert.True(JsonSemantics.Equal(birthday, CapabilityRegistry.WritableProjection("birthdays", birthday)));
        Assert.Empty(CapabilityRegistry.ValidateEditable("phoneNumbers", JsonNode.Parse("[{\"value\":\"+7 001\",\"type\":\"Приёмная\"}]")));
    }
    [Fact] public void InvalidDateAndSingletonAreRejected()
    {
        Assert.NotEmpty(CapabilityRegistry.ValidateEditable("birthdays", JsonNode.Parse("[{\"date\":{\"year\":2025,\"month\":2,\"day\":29}}]")));
        Assert.NotEmpty(CapabilityRegistry.ValidateEditable("names", JsonNode.Parse("[{},{}]")));
    }
    [Fact] public void ProfilePhoneNeverBecomesWritableContact()
    {
        var p = JsonNode.Parse("""{"resourceName":"people/1","metadata":{"sources":[{"type":"CONTACT","id":"c1"},{"type":"PROFILE","id":"p1"}]},"phoneNumbers":[{"value":"contact","metadata":{"source":{"type":"CONTACT","id":"c1"}}},{"value":"profile","metadata":{"source":{"type":"PROFILE","id":"p1"}}}]}""")!.AsObject();
        var data = PersonCodec.Data(p);
        Assert.Single(data["phoneNumbers"]!.AsArray());
        Assert.Equal("contact", data["phoneNumbers"]![0]!["value"]!.GetValue<string>());
    }
    [Fact] public void MistypedDatesAndPrimaryMetadataReturnValidationErrors()
    {
        Assert.NotEmpty(CapabilityRegistry.ValidateEditable("birthdays", JsonNode.Parse("""[{"date":{"month":"June"}}]""")));
        Assert.NotEmpty(CapabilityRegistry.ValidateEditable("names", JsonNode.Parse("""[{"givenName":"Анна","metadata":{"sourcePrimary":"yes"}}]""")));
        var doc = DocumentCodec.ToJson(new ContactDocument { Id = Guid.NewGuid() }); doc["labels"] = null;
        Assert.Throws<SyncException>(() => DocumentCodec.Contact(doc));
    }
    [Fact] public void ShippedExamplesAreAcceptedByRuntimeCodec()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var path in Directory.GetFiles(Path.Combine(root.FullName, "examples")))
        {
            var doc = JsonSemantics.ParseObject(File.ReadAllBytes(path), path);
            if (path.EndsWith(".contact.json")) DocumentCodec.Contact(doc); else DocumentCodec.Group(doc);
        }
    }
}
