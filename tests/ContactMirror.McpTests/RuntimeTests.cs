using System.Text.Json.Nodes;
using System.Text.Json;
using ContactMirror.Application;
using ContactMirror.Application.Automation;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Automation;
using ContactMirror.Infrastructure.Contacts;
using ContactMirror.Infrastructure.Demo;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.McpTests;

[NotInParallel("McpRuntime")]
public sealed partial class RuntimeTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public string Temp { get; } = Path.Combine(Path.GetTempPath(), "ContactMirrorMcpTests", Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Temp, "contacts-workspace");
        public DemoAccountConnector Account { get; } = new();
        public DemoGoogleGateway Gateway { get; }
        public FileWorkspaceStore Store { get; } = new();
        public ApplicationActivity Activity { get; } = new();
        public AgentConfiguration Configuration { get; set; }
        public AgentRuntime Runtime { get; }
        public SyncCoordinator Coordinator { get; }
        public string Bridge { get; }
        public string Workspace { get; private set; } = "";
        public Fixture(TimeProvider? timeProvider = null)
        {
            Directory.CreateDirectory(Root); Gateway = new(Path.Combine(Temp, "demo-google.json"));
            Configuration = new(true, true, true, [Root], Guid.NewGuid());
            Coordinator = new(new GuardedGoogleGateway(Gateway), Store, timeProvider: timeProvider);
            Runtime = new(Account, Coordinator, new ContactEditorStore(), Store, Activity, () => Configuration, Path.Combine(Temp, "agent-state"), timeProvider: timeProvider);
            Bridge = Runtime.ConnectBridge();
        }
        public async Task InitializeAsync()
        {
            Check(await Call("workspace_initialize", new() { ["root"] = Root, ["expectedAccountKey"] = DemoAccountConnector.Identity.Key, ["requestKey"] = Key() }));
            await OpenAsync();
        }
        public async Task OpenAsync() => Workspace = Check(await Call("workspace_open", new() { ["root"] = Root })).Data!["workspace"]!.GetValue<string>();
        public async Task OpenOn(string bridge) => Workspace = Check(await Call("workspace_open", new() { ["root"] = Root }, bridge)).Data!["workspace"]!.GetValue<string>();
        public Task<AgentResult> Call(string tool, JsonObject args, string? bridge = null) => Runtime.CallAsync(new(tool, args, bridge ?? Bridge));
        public Task<AgentResult> FinishOn(AgentResult r, string bridge) => Finish(r, bridge);
        public async Task<AgentResult> Finish(AgentResult r, string? bridge = null)
        {
            if (r.Status != "accepted") return r;
            for (var i = 0; i < 1000; i++)
            {
                var status = await Call("operations_get", new() { ["operationId"] = r.OperationId }, bridge);
                if (status.Data?["state"]?.ToString() is "completed" or "failed" or "unknown") return status.Data["result"]!.Deserialize<AgentResult>(JsonSemantics.Options)!;
                if (status.Status is "unknown" or "error") return status;
                await Task.Delay(10);
            }
            throw new TimeoutException("MCP job did not settle.");
        }
        public async Task<JsonObject> Create(string name = "Контакт агента", JsonObject? data = null)
        {
            var result = Check(await Call("contacts_create", new() { ["workspace"] = Workspace, ["requestKey"] = Key(), ["data"] = data ?? new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = name }) } }));
            return result.Data!.AsObject();
        }
        public async Task<AgentResult> Get(string id) => await Call("contacts_get", new() { ["workspace"] = Workspace, ["id"] = id });
        public async Task<AgentResult> Update(string id, string version, JsonArray patch, string? key = null) => await Call("contacts_update", new() { ["workspace"] = Workspace, ["id"] = id, ["expectedVersion"] = version, ["requestKey"] = key ?? Key(), ["patches"] = patch.DeepClone() });
        public async ValueTask DisposeAsync()
        {
            Runtime.DisconnectBridge(Bridge);
            for (var i = 0; i < 1000 && (Runtime.HasRunningJobs || Activity.IsActive); i++) await Task.Delay(10);
            Runtime.Dispose();
            if (!Runtime.HasRunningJobs && Directory.Exists(Temp)) Directory.Delete(Temp, true);
        }
    }
    private static string Key() => Guid.NewGuid().ToString();
    private static AgentResult Check(AgentResult r) => r.Status == "completed" ? r : throw new InvalidOperationException($"{r.Status}: {r.Error?.Code} {r.Error?.Message}");
    private static JsonArray Patch(string path, JsonNode? value, string op = "add") => new(new JsonObject { ["op"] = op, ["path"] = path, ["value"] = value });
    private static string Id(JsonObject result) => result["id"]!.GetValue<string>();
    private static string Ver(AgentResult r) => r.Data!["version"]!.GetValue<string>();

    [Test]
    public async Task Read_open_does_not_initialize_and_explicit_initialize_is_required()
    {
        await using var f = new Fixture(); await f.OpenAsync();
        await Assert.That(Directory.EnumerateFileSystemEntries(f.Root).Any()).IsFalse();
        var result = await f.Call("contacts_create", new() { ["workspace"] = f.Workspace, ["data"] = new JsonObject(), ["requestKey"] = Key() });
        await Assert.That(result.Error!.Code).IsEqualTo("workspace_uninitialized");
        await f.InitializeAsync(); await Assert.That(File.Exists(Path.Combine(f.Root, ".contactmirror", "workspace.json"))).IsTrue();
    }
    [Test]
    public async Task Create_patch_read_and_same_request_retry_preserve_exact_saved_contact()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var created = await f.Create(); var id = Id(created); var before = Check(await f.Get(id));
        var key = Key(); var patch = Patch("/data/phoneNumbers", new JsonArray(new JsonObject { ["value"] = "+7 999 123 45 67", ["type"] = "Работа вторая" }));
        var saved = Check(await f.Update(id, Ver(before), patch, key)); var again = Check(await f.Update(id, Ver(before), patch, key));
        await Assert.That(again.Data!.ToJsonString()).IsEqualTo(saved.Data!.ToJsonString());
        var doc = Check(await f.Get(id)).Data!["document"]!;
        await Assert.That(doc["data"]!["phoneNumbers"]![0]!["value"]!.ToString()).IsEqualTo("+7 999 123 45 67");
        await Assert.That(Directory.GetFiles(Path.Combine(f.Root, "contacts"), "*.contact.json").Length).IsEqualTo(1);
        var conflict = await f.Update(id, Ver(before), Patch("/starred", JsonValue.Create(true)));
        await Assert.That(conflict.Error!.Code).IsEqualTo("localDrift");
    }
    [Test]
    public async Task Invalid_patch_is_atomic_and_service_fields_are_protected()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var id = Id(await f.Create()); var before = Check(await f.Get(id));
        var result = await f.Update(id, Ver(before), new JsonArray(new JsonObject { ["op"] = "replace", ["path"] = "/starred", ["value"] = true }, new JsonObject { ["op"] = "replace", ["path"] = "/google", ["value"] = new JsonObject() }));
        await Assert.That(result.Error!.Code).IsEqualTo("protectedField");
        await Assert.That(Ver(Check(await f.Get(id)))).IsEqualTo(Ver(before));
    }
    [Test]
    public async Task Unknown_category_is_preserved_when_another_known_category_changes()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var id = Id(await f.Create()); var path = Path.Combine(f.Root, "contacts", id + ".contact.json");
        var doc = JsonSemantics.ParseObject(await File.ReadAllBytesAsync(path), "test"); var unknown = new JsonArray(new JsonObject { ["opaque"] = "Будущие данные", ["number"] = JsonNode.Parse("9007199254740993") });
        doc["data"]!["futureCategory"] = unknown.DeepClone(); await File.WriteAllBytesAsync(path, JsonSemantics.Serialize(doc));
        var original = Check(await f.Get(id)); var saved = Check(await f.Update(id, Ver(original), Patch("/data/phoneNumbers", new JsonArray(new JsonObject { ["value"] = "123" }))));
        var actual = Check(await f.Get(id)); await Assert.That(JsonNode.DeepEquals(actual.Data!["document"]!["data"]!["futureCategory"], unknown)).IsTrue();
        var blocked = await f.Update(id, Ver(actual), Patch("/data/futureCategory", new JsonArray(), "replace")); await Assert.That(blocked.Error!.Code).IsEqualTo("protectedCategory");
        await Assert.That(saved.Status).IsEqualTo("completed");
    }
    [Test]
    public async Task Every_writable_leaf_roundtrips_via_real_store_and_MCP_patches()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var id = Id(await f.Create()); var count = 0;
        foreach (var category in ContactFieldSchema.Fields)
        foreach (var leaf in Leaves(category))
        {
            var data = new JsonObject(); var row = new JsonObject(); data[category.Name] = new JsonArray(row);
            var parts = leaf.Path.Split('.').Skip(2).ToArray(); JsonObject current = row;
            foreach (var part in parts[..^1]) { current[part] = new JsonObject(); current = current[part]!.AsObject(); }
            var value = Sample(leaf); current[parts[^1]] = value.DeepClone();
            var before = Check(await f.Get(id)); Check(await f.Update(id, Ver(before), Patch("/data", data, "replace")));
            var actual = Check(await f.Get(id)).Data!["document"]!["data"]![category.Name]![0];
            foreach (var part in parts) actual = actual![part];
            await Assert.That(JsonNode.DeepEquals(actual, value)).IsTrue(); count++;
        }
        await Assert.That(count).IsEqualTo(111);
    }
    private static IEnumerable<ContactFieldDescriptor> Leaves(ContactFieldDescriptor f) => f.Children.Count == 0 ? [f] : f.Children.SelectMany(Leaves);
    private static JsonNode Sample(ContactFieldDescriptor f) => f.Type switch
    {
        "integer" => JsonValue.Create(f.Name == "year" ? 2026 : f.Name == "month" ? 6 : f.Name == "day" ? 15 : 1)!,
        "boolean" => JsonValue.Create(true)!,
        _ => JsonValue.Create(f.Options.FirstOrDefault()?.Value ?? (f.Name == "value" && f.Path.Contains("locales", StringComparison.Ordinal) ? "ru" : "Значение агента"))!
    };
    [Test]
    public async Task Query_alias_filters_sort_and_cursor_detect_external_drift()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await f.Create("Альфа"); await f.Create("Бета"); await f.Create("Гамма");
        var args = new JsonObject { ["workspace"] = f.Workspace, ["take"] = 1, ["sorts"] = new JsonArray(new JsonObject { ["columnId"] = "displayName", ["descending"] = true }) };
        var first = Check(await f.Call("contacts_list", args)); await Assert.That(first.Data!["items"]![0]!["values"]!["displayName"]!.ToString()).IsEqualTo("Гамма");
        args["cursor"] = first.Data!["nextCursor"]!.DeepClone(); await f.Create("Дельта");
        var stale = await f.Call("contacts_list", args); await Assert.That(stale.Error!.Code).IsEqualTo("cursor_stale");
        var filtered = Check(await f.Call("contacts_list", new() { ["workspace"] = f.Workspace, ["filters"] = new JsonArray(new JsonObject { ["columnId"] = "displayName", ["operator"] = "Contains", ["value"] = "Аль" }) }));
        await Assert.That(filtered.Data!["total"]!.GetValue<int>()).IsEqualTo(1);
    }
    private sealed class DraftUi(AgentUiState state) : IAgentUi
    {
        public Task<AgentUiState> GetStateAsync() => Task.FromResult(state);
        public Task ShowWindowAsync() => Task.CompletedTask;
        public Task OpenContactAsync(string root, Guid id) => Task.CompletedTask;
        public Task ChangedAsync(string? root) => Task.CompletedTask;
    }
    [Test]
    public async Task Dirty_selected_contact_is_blocked_unrelated_contact_and_status_work()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var first = await f.Create(); var second = await f.Create();
        f.Runtime.Ui = new DraftUi(new(f.Root, Guid.Parse(Id(first)), true, true));
        var blocked = await f.Update(Id(first), first["version"]!.ToString(), Patch("/starred", JsonValue.Create(true))); await Assert.That(blocked.Error!.Code).IsEqualTo("draft_conflict");
        Check(await f.Update(Id(second), second["version"]!.ToString(), Patch("/starred", JsonValue.Create(true))));
        Check(await f.Call("app_get_state", new()));
    }
    [Test]
    public async Task Shared_UI_lease_blocks_mutations_but_not_status()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var c = await f.Create();
        using var lease = f.Activity.TryEnter()!;
        var blocked = await f.Update(Id(c), c["version"]!.ToString(), Patch("/starred", JsonValue.Create(true))); await Assert.That(blocked.Error!.Code).IsEqualTo("busy");
        Check(await f.Call("app_get_state", new()));
    }
    [Test]
    public async Task Idempotency_key_cannot_be_reused_for_a_different_contact_request()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var key = Key(); var a = new JsonObject { ["workspace"] = f.Workspace, ["requestKey"] = key, ["data"] = new JsonObject() };
        Check(await f.Call("contacts_create", a)); a["starred"] = true;
        var rejected = await f.Call("contacts_create", a); await Assert.That(rejected.Error!.Code).IsEqualTo("idempotency_conflict");
        await Assert.That(Directory.GetFiles(Path.Combine(f.Root, "contacts"), "*.contact.json").Length).IsEqualTo(1);
    }
    [Test]
    public async Task Readonly_and_revoked_generation_are_enforced_by_runtime()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var c = await f.Create();
        f.Configuration = f.Configuration with { LocalWrite = false, Generation = Guid.NewGuid() };
        var old = await f.Get(Id(c)); await Assert.That(old.Error!.Code).IsEqualTo("access_revoked"); var bridge = f.Runtime.ConnectBridge();
        Check(await f.Call("contacts_get", new() { ["workspace"] = f.Workspace, ["id"] = Id(c) }, bridge));
        var write = await f.Call("contacts_update", new() { ["workspace"] = f.Workspace, ["id"] = Id(c), ["expectedVersion"] = c["version"]!.DeepClone(), ["requestKey"] = Key(), ["patches"] = Patch("/starred", JsonValue.Create(true)) }, bridge);
        await Assert.That(write.Error!.Code).IsEqualTo("permission_denied"); f.Runtime.DisconnectBridge(bridge);
    }
}
