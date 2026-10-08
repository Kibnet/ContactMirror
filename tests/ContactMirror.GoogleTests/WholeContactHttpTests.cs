using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Core;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Demo;
using ContactMirror.Infrastructure.Google;
using SkiaSharp;
using Xunit;

namespace ContactMirror.GoogleTests;

// Real coordinator and filesystem with the production HTTP boundary; all server data is synthetic.
public sealed class WholeContactHttpTests : IAsyncDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "ContactMirror-WholeContact-" + Guid.NewGuid().ToString("N"));
    private readonly FileWorkspaceStore store = new();
    private string Root => Path.Combine(temp, "workspace");
    private readonly Api api;
    private readonly Tokens tokens = new();
    private readonly HttpClient http;
    private readonly SyncCoordinator coordinator;
    public WholeContactHttpTests()
    {
        Directory.CreateDirectory(Root); api = new(new DemoGoogleGateway(Path.Combine(temp, "remote.json"))); http = new(api);
        coordinator = new(new GoogleContactsGateway(tokens, http), store);
    }
    public ValueTask DisposeAsync() { http.Dispose(); Directory.Delete(temp, true); return ValueTask.CompletedTask; }
    private static IReadOnlyList<PlanChoice> Defaults(SyncPreview p) => p.Entries.Where(e => e.DefaultSelected).Select(e => new PlanChoice(e.Key)).ToArray();
    private async Task Export()
    {
        var p = await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity); Assert.True((await coordinator.ApplyAsync(p, Defaults(p))).IsComplete);
    }
    private (string Path, JsonObject Doc) Contact(string resource) => Directory.GetFiles(Path.Combine(Root, "contacts"), "*.json")
        .Select(p => (Path: p, Doc: JsonNode.Parse(File.ReadAllText(p))!.AsObject())).Single(x => x.Doc["google"]?["resourceName"]?.ToString() == resource);
    private static byte[] Image(SKColor color)
    {
        using var bitmap = new SKBitmap(32, 32); bitmap.Erase(color); using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 90); return encoded.ToArray();
    }
    private static JsonArray Value(string field) => JsonNode.Parse(field switch
    {
        "names" => "[{\"familyName\":\"Петренко\",\"givenName\":\"Иван\"}]",
        "addresses" => "[{\"city\":\"Город\",\"streetAddress\":\"Тестовая 1\"}]",
        "birthdays" or "events" => "[{\"date\":{\"year\":2000,\"month\":5,\"day\":12}}]",
        "clientData" or "userDefined" => "[{\"key\":\"test\",\"value\":\"new\"}]",
        "organizations" => "[{\"name\":\"Организация\",\"title\":\"Инженер\"}]",
        "calendarUrls" => "[{\"url\":\"https://example.test/new\"}]",
        "urls" => "[{\"value\":\"https://example.test/new\"}]",
        "imClients" => "[{\"username\":\"test\",\"protocol\":\"test\"}]",
        "relations" => "[{\"person\":\"Тест\",\"type\":\"friend\"}]",
        "biographies" => "[{\"value\":\"Заметка\",\"contentType\":\"TEXT_PLAIN\"}]",
        "locales" => "[{\"value\":\"ru\"}]",
        "phoneNumbers" => "[{\"value\":\"+1 202 555 0199\"}]",
        "emailAddresses" => "[{\"value\":\"new@example.test\"}]",
        _ => "[{\"value\":\"new\"}]"
    })!.AsArray();

    [Fact] public async Task AllCategoriesMembershipsAndPhotoUseExactApiContractsPreserveProfileAndConverge()
    {
        await Export(); var (path, doc) = Contact("people/demo5"); var id = Guid.Parse(doc["id"]!.ToString());
        var original = (await api.Remote.GetPersonAsync("people/demo5"))!;
        foreach (var field in CapabilityRegistry.WritableFields) { var sample = Value(field); Assert.Empty(CapabilityRegistry.ValidateEditable(field, sample)); doc["data"]![field] = sample; }
        doc["labels"] = new JsonArray(); doc["starred"] = !doc["starred"]!.GetValue<bool>();
        var image = Image(SKColors.Coral); doc["photo"] = "photos/new.png"; await File.WriteAllBytesAsync(Path.Combine(Root, "photos", "new.png"), image);
        doc["google"]!["person"]!["names"]![0]!["familyName"] = "Петренко";
        await File.WriteAllBytesAsync(path, JsonSemantics.Serialize(doc));
        var p = await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity); var blocked = Assert.Single(p.Entries, e => e.EntityId == id);
        var calls = api.Calls; var tokenCalls = tokens.Calls; api.Reject = true; tokens.Reject = true;
        Assert.True((await coordinator.RepairGoogleSnapshotAsync(p, blocked.Key)).IsComplete); Assert.Equal(calls, api.Calls); Assert.Equal(tokenCalls, tokens.Calls);
        api.Reject = false; tokens.Reject = false;
        p = await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity);
        Assert.All(p.Entries.Where(e => e.EntityId == id), e => Assert.True(e.IsSelectable));
        Assert.True((await coordinator.ApplyAsync(p, Defaults(p))).IsComplete);
        Assert.Equal(CapabilityRegistry.WritableFields.Order(), api.Masks.SelectMany(m => m).Order()); Assert.All(api.Masks, m => Assert.Single(m));
        var final = (await api.Remote.GetPersonAsync("people/demo5"))!;
        foreach (var field in CapabilityRegistry.WritableFields) Assert.True(JsonSemantics.Equal(Value(field), PersonCodec.Data(final)[field]), field);
        var profileBefore = original["emailAddresses"]!.AsArray().Where(v => !PersonCodec.IsContactValue(v, original)).Select(JsonSemantics.Clone).ToArray();
        var profileAfter = final["emailAddresses"]!.AsArray().Where(v => !PersonCodec.IsContactValue(v, final)).Select(JsonSemantics.Clone).ToArray();
        Assert.True(JsonSemantics.Equal(new JsonArray(profileBefore), new JsonArray(profileAfter)));
        Assert.Equal(PersonCodec.Memberships(original).Where(x => x != "contactGroups/starred" && !x.StartsWith("contactGroups/demo-")), PersonCodec.Memberships(final).Where(x => x != "contactGroups/starred" && !x.StartsWith("contactGroups/demo-")));
        Assert.Equal(image, await File.ReadAllBytesAsync(Path.Combine(Root, "photos", "new.png")));
        p = await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity); Assert.DoesNotContain(p.Entries, e => e.EntityId == id);
        if (p.Entries.Count > 0) { Assert.All(p.Entries, e => Assert.Equal(ChangeKind.Snapshot, e.Kind)); await coordinator.ApplyAsync(p, Defaults(p)); }
        Assert.Empty((await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity)).Entries);
    }

    [Fact] public async Task RepairTransactionRollbackKeepsRecoveryIntentAndHistoryUntilRestartCompletes()
    {
        await Export(); var (path, doc) = Contact("people/demo1"); var id = Guid.Parse(doc["id"]!.ToString());
        doc["google"]!["person"]!["names"]![0]!["familyName"] = "Служебная правка";
        await File.WriteAllBytesAsync(path, JsonSemantics.Serialize(doc));
        var faultStore = new FileWorkspaceStore(null, afterRepairIntentResolution: () => throw new IOException("Injected interruption inside repair transaction"));
        var faulted = new SyncCoordinator(new GoogleContactsGateway(tokens, http), faultStore);
        var preview = await faulted.PrepareAsync(Root, DemoAccountConnector.Identity); var entry = Assert.Single(preview.Entries, e => e.EntityId == id);
        var failed = await faulted.RepairGoogleSnapshotAsync(preview, entry.Key); Assert.Equal(1, failed.Unknown);
        await using (var session = await store.OpenAsync(Root, DemoAccountConnector.Identity))
        {
            Assert.Single(session.View.Pending, o => o.EntityId == id && o.Field == "$repairGoogleSnapshot");
            await Assert.ThrowsAsync<SyncException>(() => session.DeleteBackupAsync(failed.RunId));
        }
        Assert.Equal("partial", Assert.Single(await store.GetHistoryAsync(Root), r => r.Id == failed.RunId).Status);
        var restarted = new SyncCoordinator(new GoogleContactsGateway(tokens, http), store);
        preview = await restarted.PrepareAsync(Root, DemoAccountConnector.Identity); entry = Assert.Single(preview.Entries, e => e.EntityId == id);
        Assert.True(entry.CompletesLocalRepair);
        var calls = api.Calls; var tokenCalls = tokens.Calls; api.Reject = true; tokens.Reject = true;
        var success = await restarted.RepairGoogleSnapshotAsync(preview, entry.Key); Assert.True(success.IsComplete);
        Assert.Equal(calls, api.Calls); Assert.Equal(tokenCalls, tokens.Calls); api.Reject = false; tokens.Reject = false;
        await using (var session = await store.OpenAsync(Root, DemoAccountConnector.Identity)) { Assert.Empty(session.View.Pending); await session.DeleteBackupAsync(failed.RunId); }
        var history = await store.GetHistoryAsync(Root);
        Assert.Equal("recovered", Assert.Single(history, r => r.Id == failed.RunId).Status); Assert.Equal("complete", Assert.Single(history, r => r.Id == success.RunId).Status);
        Assert.Empty((await restarted.PrepareAsync(Root, DemoAccountConnector.Identity)).Entries);
    }

    [Theory, InlineData("add"), InlineData("replace"), InlineData("delete")]
    public async Task PhotoChangeReadbackReencodingAndNextNoOp(string mode)
    {
        if (mode == "add") await api.Remote.UpdatePhotoAsync("people/demo1", null);
        await Export(); var (path, doc) = Contact("people/demo1"); var id = Guid.Parse(doc["id"]!.ToString()); var oldPath = doc["photo"]?.ToString();
        var image = Image(SKColors.Coral);
        if (mode == "delete") doc["photo"] = null;
        else { doc["photo"] ??= "photos/new.png"; await File.WriteAllBytesAsync(Path.Combine(Root, doc["photo"]!.ToString()), image); }
        await File.WriteAllBytesAsync(path, JsonSemantics.Serialize(doc));
        var p = await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity); var e = Assert.Single(p.Entries, e => e.EntityId == id && e.Field == "photo");
        Assert.Equal(ChangeKind.Upload, e.Kind); Assert.NotNull(e.PhotoComparison); if (mode == "replace") Assert.Equal(oldPath, e.PhotoComparison.LocalPath);
        api.Reencode = true; var run = await coordinator.ApplyAsync(p, Defaults(p)); Assert.True(run.IsComplete);
        Assert.Equal(mode == "delete" ? "delete" : "update", Assert.Single(api.PhotoActions));
        p = await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity); Assert.Empty(p.Entries);
        await using var session = await store.OpenAsync(Root, DemoAccountConnector.Identity); var state = session.View.States[id];
        if (mode != "delete") { Assert.NotEqual(state.PhotoLocalHash, state.PhotoRemoteHash); Assert.Equal(image, session.View.Contacts[id].PhotoBytes); }
        else { Assert.Null(state.PhotoLocalHash); Assert.Null(state.PhotoRemoteHash); }
    }
    [Fact] public async Task PhotoConflictAndFailedPhotoKeepTextAndRequireExplicitRetry()
    {
        await Export(); var (path, doc) = Contact("people/demo1"); var id = Guid.Parse(doc["id"]!.ToString());
        await File.WriteAllBytesAsync(Path.Combine(Root, doc["photo"]!.ToString()), Image(SKColors.Coral));
        await api.Remote.UpdatePhotoAsync("people/demo1", Image(SKColors.Green));
        var p = await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity); var conflict = Assert.Single(p.Entries, e => e.EntityId == id && e.Field == "photo"); Assert.Equal(ChangeKind.Conflict, conflict.Kind);
        Assert.Empty(api.PhotoActions); Assert.Contains("Разные правки", conflict.PhotoComparison!.Status);
        doc["data"]!["names"] = Value("names"); await File.WriteAllBytesAsync(path, JsonSemantics.Serialize(doc));
        p = await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity); api.FailPhoto = true;
        var choices = p.Entries.Where(e => e.DefaultSelected || e.Field == "photo").Select(e => new PlanChoice(e.Key, e.Kind == ChangeKind.Conflict ? Resolution.UseLocal : Resolution.Automatic)).ToArray();
        var run = await coordinator.ApplyAsync(p, choices); Assert.False(run.IsComplete); Assert.Single(api.PhotoActions); Assert.Single(api.Masks);
        p = await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity); Assert.DoesNotContain(p.Entries, e => e.EntityId == id && e.Field == "names");
        Assert.Contains(p.Entries, e => e.EntityId == id && e.Field == "photo");
        api.FailPhoto = false; choices = p.Entries.Where(e => e.DefaultSelected || e.Field == "photo").Select(e => new PlanChoice(e.Key, e.Kind == ChangeKind.Conflict ? Resolution.UseLocal : Resolution.Automatic)).ToArray();
        Assert.True((await coordinator.ApplyAsync(p, choices)).IsComplete); Assert.Empty((await coordinator.PrepareAsync(Root, DemoAccountConnector.Identity)).Entries); Assert.Single(api.Masks);
    }

    private sealed class Tokens : IAccessTokenProvider
    {
        public int Calls; public bool Reject;
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) { Calls++; if (Reject) throw new InvalidOperationException("Token must not be requested"); return Task.FromResult("synthetic"); }
    }
    private sealed class Api(DemoGoogleGateway remote) : HttpMessageHandler
    {
        public DemoGoogleGateway Remote => remote;
        public int Calls; public bool Reject; public bool Reencode; public bool FailPhoto;
        public List<string[]> Masks { get; } = []; public List<string> PhotoActions { get; } = [];
        private static HttpResponseMessage Json(JsonNode node) => new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; if (Reject) throw new InvalidOperationException("HTTP must not be called");
            var uri = request.RequestUri!; var path = uri.AbsolutePath.Replace("/v1/", "");
            if (uri.Host.EndsWith("googleusercontent.com"))
            {
                Assert.Null(request.Headers.Authorization);
                var content = new ByteArrayContent(await remote.DownloadPhotoAsync(uri.AbsoluteUri, ct)); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return new(HttpStatusCode.OK) { Content = content };
            }
            Assert.Equal("synthetic", request.Headers.Authorization?.Parameter);
            if (path == "people/me/connections") return Json(new JsonObject { ["connections"] = new JsonArray((await remote.ReadAllAsync(cancellationToken: ct)).People.Select(p => (JsonNode)p).ToArray()) });
            if (path == "contactGroups") return Json(new JsonObject { ["contactGroups"] = new JsonArray((await remote.ReadAllAsync(cancellationToken: ct)).Groups.Select(g => (JsonNode)g).ToArray()) });
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!.AsObject();
            var resource = path.Split(':')[0];
            if (path.EndsWith(":updateContact"))
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                var fields = Uri.UnescapeDataString(uri.Query).Split('&').Single(x => x.StartsWith("updatePersonFields=")).Split('=')[1].Split(','); Masks.Add(fields);
                var source = Assert.Single(body!["metadata"]!["sources"]!.AsArray()); Assert.Equal("CONTACT", source!["type"]!.ToString()); Assert.Equal(PersonCodec.Source((await remote.GetPersonAsync(resource, ct))!)["etag"]!.ToString(), source["etag"]!.ToString());
                Assert.Equal(fields.Order(), body.Where(x => x.Key != "metadata").Select(x => x.Key).Order());
                return Json(await remote.UpdateContactAsync(resource, body, fields, ct));
            }
            if (path.EndsWith(":updateContactPhoto") || path.EndsWith(":deleteContactPhoto"))
            {
                var delete = request.Method == HttpMethod.Delete; PhotoActions.Add(delete ? "delete" : "update");
                if (FailPhoto) return new(HttpStatusCode.BadRequest) { Content = new StringContent("{}") };
                var bytes = delete ? null : Convert.FromBase64String(body!["photoBytes"]!.ToString()); if (Reencode && bytes is not null) bytes = bytes.Concat(new byte[] { 0 }).ToArray();
                return Json(new JsonObject { ["person"] = await remote.UpdatePhotoAsync(resource, bytes, ct) });
            }
            if (path.EndsWith("/members:modify"))
            {
                var group = path[..^"/members:modify".Length]; var add = body!.ContainsKey("resourceNamesToAdd");
                await remote.ModifyMembershipAsync(group, body[add ? "resourceNamesToAdd" : "resourceNamesToRemove"]![0]!.ToString(), add, ct); return Json(new JsonObject());
            }
            if (path.StartsWith("people/")) return Json((await remote.GetPersonAsync(resource, ct))!);
            if (path.StartsWith("contactGroups/")) return Json((await remote.GetGroupAsync(resource, ct))!);
            throw new InvalidOperationException("Unexpected fake API route " + path);
        }
    }
}
