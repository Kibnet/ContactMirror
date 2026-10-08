using System.Diagnostics;
using System.Text.Json.Nodes;
using ContactMirror.Core;
using Xunit;

namespace ContactMirror.Tests;

public sealed class PreviewDiffTests
{
    private static SyncEntry Entry(string before, string local, string google, string field = "names") => new() { Key = "test", EntityId = Guid.NewGuid(), Name = "Тест", Field = field, Kind = ChangeKind.Upload, Before = JsonNode.Parse(before), Local = JsonNode.Parse(local), Google = JsonNode.Parse(google) };
    [Fact] public void SingletonNameExplainsExactLeafChangesQuickly()
    {
        var e = Entry("[{\"familyName\":\"Петров\",\"givenName\":\"Иван\"}]", "[{\"familyName\":\"Петренко\",\"givenName\":\"Иван\"}]", "[{\"familyName\":\"Петров\",\"givenName\":\"Иван\"}]");
        PreviewDiff.Build(e); var watch = Stopwatch.StartNew(); var row = Assert.Single(PreviewDiff.Build(e)); watch.Stop();
        Assert.Equal("Фамилия", row.Title); Assert.Equal("Петров", row.Before); Assert.Equal("Петренко", row.Local); Assert.Equal("Петров", row.Google); Assert.True(watch.ElapsedMilliseconds < 100);
    }
    [Fact] public void ArrayReorderIsNotAnEditAndDuplicateRemovalIsVisible()
    {
        const string b = "[{\"value\":\"A\"},{\"value\":\"A\"},{\"value\":\"B\"}]";
        Assert.Empty(PreviewDiff.Build(Entry(b, "[{\"value\":\"B\"},{\"value\":\"A\"},{\"value\":\"A\"}]", b, "phoneNumbers")));
        var row = Assert.Single(PreviewDiff.Build(Entry(b, "[{\"value\":\"A\"},{\"value\":\"B\"}]", b, "phoneNumbers")));
        Assert.Contains("A", row.Before); Assert.Equal("Нет поля", row.Local); Assert.Contains("A", row.Google);
    }
    [Fact] public void MultipleUnrelatedPhonesAreNotGuessedAsOneReplacement()
    {
        var rows = PreviewDiff.Build(Entry("[{\"value\":\"A\"},{\"value\":\"B\"}]", "[{\"value\":\"C\"},{\"value\":\"B\"}]", "[{\"value\":\"A\"},{\"value\":\"D\"}]", "phoneNumbers"));
        Assert.Equal(4, rows.Count); Assert.DoesNotContain(rows, r => r.Before.Contains("A") && r.Local.Contains("C"));
    }
    [Fact] public void MissingNullEmptyStringAndArrayRemainDistinct()
    {
        var rows = PreviewDiff.Build(Entry("{\"a\":null,\"b\":\"\",\"c\":[]}", "{\"a\":\"\",\"b\":[],\"d\":null}", "{\"a\":null,\"b\":\"\",\"c\":[]}"));
        Assert.Equal(4, rows.Count); Assert.Equal("null", rows.Single(r => r.Path.EndsWith(".a")).Before); Assert.Equal("Нет поля", rows.Single(r => r.Path.EndsWith(".d")).Before);
        Assert.Equal("[]", rows.Single(r => r.Path.EndsWith(".c")).Before); Assert.Contains("пустая строка", rows.Single(r => r.Path.EndsWith(".b")).Before);
    }
    [Fact] public void CancelledAndLargeDiffDoesNotSilentlyTruncate()
    {
        var before = new JsonArray(Enumerable.Range(0, 2000).Select(i => (JsonNode)JsonValue.Create(i)).ToArray()).ToJsonString();
        var local = new JsonArray(Enumerable.Range(2000, 2000).Select(i => (JsonNode)JsonValue.Create(i)).ToArray()).ToJsonString();
        var e = Entry(before, local, before, "phoneNumbers");
        Assert.Throws<OperationCanceledException>(() => PreviewDiff.Build(e, new CancellationToken(true)));
        Assert.Equal(4000, PreviewDiff.Build(e).Count);
    }
    [Fact] public void ObjectNullAndMissingTransitionsRetainParentType()
    {
        var rows = PreviewDiff.Build(Entry("{\"date\":{\"year\":2000}}", "{\"date\":null}", "{}"));
        var parent = Assert.Single(rows, r => r.Path.EndsWith(".date"));
        Assert.Equal("Объект", parent.Before); Assert.Equal("null", parent.Local); Assert.Equal("Нет поля", parent.Google);
        Assert.Contains(rows, r => r.Path.EndsWith(".date.year"));
    }
    [Fact] public void MissingArrayRemainsDifferentFromEmptyArrayWithRemoteAddition()
    {
        var rows = PreviewDiff.Build(Entry("{\"phoneNumbers\":[]}", "{}", "{\"phoneNumbers\":[\"A\"]}"));
        var presence = Assert.Single(rows, r => r.Path.EndsWith(".phoneNumbers"));
        Assert.Equal("[]", presence.Before); Assert.Equal("Нет поля", presence.Local); Assert.Equal("Массив (1)", presence.Google);
        Assert.Contains(rows, r => r.Google == "A");
    }
    [Fact] public void SinglePhoneReplacementDoesNotInventIdentityAndPathsUseSideIndices()
    {
        var rows = PreviewDiff.Build(Entry("[{\"value\":\"A\"}]", "[{\"value\":\"B\"}]", "[{\"value\":\"A\"}]", "phoneNumbers"));
        Assert.Equal(2, rows.Count); Assert.DoesNotContain(rows, r => r.Before.Contains("A") && r.Local.Contains("B"));
        rows = PreviewDiff.Build(Entry("[1,2,3]", "[3,1]", "[2,3]", "phoneNumbers"));
        var row = Assert.Single(rows, r => r.Before == "1"); Assert.Equal("$.data.phoneNumbers[0]", row.BeforePath); Assert.Equal("$.data.phoneNumbers[1]", row.LocalPath); Assert.Null(row.GooglePath);
        row = Assert.Single(rows, r => r.Before == "2"); Assert.Equal("$.data.phoneNumbers[1]", row.BeforePath); Assert.Equal("$.data.phoneNumbers[0]", row.GooglePath);
    }
}
