using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Core;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Google;
using SkiaSharp;

if (args.Length != 3 || args[2] != "--execute-authorized-fixture") throw new ArgumentException("GoogleSmoke <IntegrationValidation install> <new fixture directory> --execute-authorized-fixture");
var install = Path.GetFullPath(args[0]);
if (Path.GetFileName(install) != "ContactMirror.IntegrationValidation") throw new InvalidOperationException("Only the isolated Google validation install is allowed.");
var root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) throw new InvalidOperationException("Fixture directory already exists. Inspect its journal; never repeat an ambiguous creation automatically.");
Directory.CreateDirectory(root);
var state = new FixtureState { Marker = "ContactMirror TEST 20261007 " + Guid.NewGuid().ToString("N") };
var journal = Path.Combine(root, "fixture-journal.json");
void Save() => File.WriteAllText(journal, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
Save();
var options = OAuthClientOptions.Load(Path.Combine(install, "current", "appsettings", "oauth-client.json")) with { ConfigurationSource = "publisher" };
var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContactMirror.IntegrationValidation.Data");
var accountService = new GoogleAccountService(options, new WindowsCredentialVault(Path.Combine(data, "Credentials", "google.dpapi")));
var account = await accountService.GetAccountAsync() ?? throw new InvalidOperationException("The installed Google account is not connected.");
var gateway = new FixtureGateway(new GoogleContactsGateway(accountService), state, Save);
var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
var coordinator = new SyncCoordinator(gateway, new FileWorkspaceStore());
var checks = new List<string>();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
var token = timeout.Token;
try
{
    await using (var initial = await new FileWorkspaceStore().OpenAsync(workspace, account, token)) { }
    var label = new GroupDocument { Id = Guid.NewGuid(), Name = state.Marker, ClientData = new JsonArray(new JsonObject { ["key"] = "fixture", ["value"] = state.Marker }) };
    var contact = new ContactDocument { Id = Guid.NewGuid(), Labels = [label.Id], Starred = true, Photo = "photos/fixture.png", Data = JsonNode.Parse("""
    {"names":[{"givenName":"ContactMirror TEST 20261007"}],"phoneNumbers":[{"value":"+1 202 555 0100","type":"mobile"}],"emailAddresses":[{"value":"contactmirror-fixture@example.invalid","type":"work"}],"biographies":[{"value":"Вымышленный проверочный контакт","contentType":"TEXT_PLAIN"}],"birthdays":[{"date":{"year":1990,"month":5,"day":21}}],"organizations":[{"name":"ContactMirror Fixture","title":"Проверка","type":"work"}],"addresses":[{"streetAddress":"Fixture Street 1","city":"Fixture","countryCode":"US","type":"work"}],"userDefined":[{"key":"Unicode","value":"Проверка Ёж 🦔"}],"clientData":[{"key":"fixture","value":"contactmirror-live-smoke"}],"sipAddresses":[{"value":"sip:fixture@example.invalid","type":"work"}],"locales":[{"value":"ru-RU"}],"urls":[{"value":"https://example.invalid/fixture","type":"work"}]}
    """)!.AsObject() };
    contact.Data["names"]![0]!["givenName"] = state.Marker;
    var contactPath = Path.Combine(workspace, "contacts", $"{contact.Id}.contact.json");
    await File.WriteAllBytesAsync(contactPath, JsonSemantics.Serialize(DocumentCodec.ToJson(contact)), token);
    await File.WriteAllBytesAsync(Path.Combine(workspace, "groups", $"{label.Id}.group.json"), JsonSemantics.Serialize(DocumentCodec.ToJson(label)), token);
    using (var bitmap = new SKBitmap(64, 64))
    { bitmap.Erase(SKColors.CornflowerBlue); using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 90); await File.WriteAllBytesAsync(Path.Combine(workspace, "photos", "fixture.png"), encoded.ToArray(), token); }
    await Apply("create-from-json", allowedRemote: true);
    var remote = await Readback();
    Require(PersonCodec.Data(remote)["phoneNumbers"]?.ToJsonString().Contains("0100") == true, "Initial phone missing.");
    Require(PersonCodec.Data(remote)["userDefined"]?.ToJsonString().Contains("Unicode") == true, "Unicode field missing.");
    Require(PersonCodec.Memberships(remote).Contains(state.Group!) && PersonCodec.Memberships(remote).Contains("contactGroups/starred"), "Label/star membership missing.");
    Require(PersonCodec.PhotoUrl(remote) is not null, "Photo missing.");
    checks.Add("create-fields-photo-label-star-readback");
    var file = DocumentCodec.Contact(JsonSemantics.ParseObject(await File.ReadAllBytesAsync(contactPath, token), "fixture"));
    file.Data["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = "+1 202 555 0101", ["type"] = "mobile" });
    await File.WriteAllBytesAsync(contactPath, JsonSemantics.Serialize(DocumentCodec.ToJson(file)), token);
    await Apply("file-phone-upload", allowedRemote: true);
    remote = await Readback();
    Require(PersonCodec.Data(remote)["phoneNumbers"]?.ToJsonString().Contains("0101") == true, "Uploaded phone missing.");
    checks.Add("file-phone-upload-readback");
    var body = new JsonObject { ["resourceName"] = state.Person, ["etag"] = remote["etag"]?.DeepClone(), ["metadata"] = new JsonObject { ["sources"] = new JsonArray(PersonCodec.Source(remote).DeepClone()) }, ["biographies"] = new JsonArray(new JsonObject { ["value"] = "Изменено на стороне Google: Ёж 🦔", ["contentType"] = "TEXT_PLAIN" }) };
    await gateway.UpdateContactAsync(state.Person!, body, ["biographies"], token);
    await Apply("google-biography-download", allowedRemote: false);
    file = DocumentCodec.Contact(JsonSemantics.ParseObject(await File.ReadAllBytesAsync(contactPath, token), "fixture"));
    Require(file.Data["biographies"]?[0]?["value"]?.GetValue<string>() == "Изменено на стороне Google: Ёж 🦔", "Downloaded biography missing.");
    checks.Add("google-edit-download");
    var next = await coordinator.PrepareAsync(workspace, account, cancellationToken: token);
    Require(next.Entries.Count == 0, "Expected no-op, received: " + string.Join(',', next.Entries.Select(e => e.Field + ":" + e.Kind)));
    checks.Add("next-no-op");
    state.Result = "PASS"; Save();
}
catch (Exception error)
{
    state.Result = "FAIL"; state.ErrorCode = error is SyncException sync ? sync.Code : error.GetType().Name; Save();
    throw;
}
finally
{
    // Only IDs returned by these two creations may be deleted, even after a partial failure.
    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    if (state.Person is not null) { await gateway.DeleteContactAsync(state.Person, cleanupTimeout.Token); state.PersonDeleted = true; Save(); }
    if (state.Group is not null) { await gateway.DeleteGroupAsync(state.Group, cleanupTimeout.Token); state.GroupDeleted = true; Save(); }
    state.CleanupVerified = (state.Person is null || await gateway.GetPersonAsync(state.Person, cleanupTimeout.Token) is null)
        && (state.Group is null || await gateway.GetGroupAsync(state.Group, cleanupTimeout.Token) is null);
    Save();
    Console.WriteLine(JsonSerializer.Serialize(new { state.Result, checks, state.PersonDeleted, state.GroupDeleted, state.CleanupVerified, state.ErrorCode }));
}
async Task<JsonObject> Readback() => await gateway.GetPersonAsync(state.Person!, token) ?? throw new InvalidOperationException("Created fixture missing.");
async Task Apply(string step, bool allowedRemote)
{
    state.Step = step; Save();
    var preview = await coordinator.PrepareAsync(workspace, account, cancellationToken: token);
    Require(preview.Entries.All(e => e.DefaultSelected && !e.IsDestructive), "Unexpected conflict, blocked or deletion in fixture.");
    if (!allowedRemote) Require(preview.Entries.All(e => e.Kind is not (ChangeKind.Upload or ChangeKind.CreateRemote)), "Unexpected remote write during download.");
    var result = await coordinator.ApplyAsync(preview, preview.Entries.Select(e => new PlanChoice(e.Key)).ToArray(), cancellationToken: token);
    Require(result.IsComplete, "Incomplete fixture run: " + string.Join(';', result.Operations.Where(o => o.Status != "confirmed").Select(o => o.Message)));
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
sealed class FixtureState
{
    public string Marker { get; set; } = ""; public string? Person { get; set; } public string? Group { get; set; }
    public bool PersonCreateAttempted { get; set; } public bool GroupCreateAttempted { get; set; }
    public string Step { get; set; } = "prepared"; public string Result { get; set; } = "RUNNING"; public string? ErrorCode { get; set; }
    public bool PersonDeleted { get; set; } public bool GroupDeleted { get; set; } public bool CleanupVerified { get; set; }
}
sealed class FixtureGateway(IGoogleContactsGateway inner, FixtureState state, Action save) : IGoogleContactsGateway
{
    public async Task<RemoteSnapshot> ReadAllAsync(IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var person = state.Person is null ? null : await GetPersonAsync(state.Person, cancellationToken);
        var group = state.Group is null ? null : await GetGroupAsync(state.Group, cancellationToken);
        return new(person is null ? [] : [person], group is null ? [] : [group]);
    }
    private void Person(string resource) { if (resource != state.Person || state.Person is null) throw new InvalidOperationException("Write outside fixture rejected."); }
    private void Group(string resource) { if (resource != state.Group || state.Group is null) throw new InvalidOperationException("Write outside fixture rejected."); }
    public Task<JsonObject?> GetPersonAsync(string resourceName, CancellationToken cancellationToken = default) { Person(resourceName); return inner.GetPersonAsync(resourceName, cancellationToken); }
    public Task<JsonObject?> GetGroupAsync(string resourceName, CancellationToken cancellationToken = default) { Group(resourceName); return inner.GetGroupAsync(resourceName, cancellationToken); }
    public async Task<JsonObject> CreateContactAsync(JsonObject person, CancellationToken cancellationToken = default)
    {
        if (state.PersonCreateAttempted || person["names"]?[0]?["givenName"]?.GetValue<string>() != state.Marker) throw new InvalidOperationException("Repeated or unmarked creation rejected.");
        state.PersonCreateAttempted = true; save();
        var created = await inner.CreateContactAsync(person, cancellationToken); state.Person = PersonCodec.Resource(created); save(); return created;
    }
    public async Task<JsonObject> CreateGroupAsync(string name, JsonArray clientData, CancellationToken cancellationToken = default)
    {
        if (state.GroupCreateAttempted || name != state.Marker) throw new InvalidOperationException("Repeated or unmarked creation rejected.");
        state.GroupCreateAttempted = true; save();
        var created = await inner.CreateGroupAsync(name, clientData, cancellationToken); state.Group = created["resourceName"]!.GetValue<string>(); save(); return created;
    }
    public Task<JsonObject> UpdateContactAsync(string resourceName, JsonObject person, IReadOnlyCollection<string> fields, CancellationToken cancellationToken = default) { Person(resourceName); return inner.UpdateContactAsync(resourceName, person, fields, cancellationToken); }
    public Task DeleteContactAsync(string resourceName, CancellationToken cancellationToken = default) { Person(resourceName); return inner.DeleteContactAsync(resourceName, cancellationToken); }
    public Task<JsonObject> UpdatePhotoAsync(string resourceName, byte[]? bytes, CancellationToken cancellationToken = default) { Person(resourceName); return inner.UpdatePhotoAsync(resourceName, bytes, cancellationToken); }
    public Task<byte[]> DownloadPhotoAsync(string url, CancellationToken cancellationToken = default) => inner.DownloadPhotoAsync(url, cancellationToken);
    public Task<JsonObject> UpdateGroupAsync(string resourceName, string name, JsonArray clientData, CancellationToken cancellationToken = default) { Group(resourceName); return inner.UpdateGroupAsync(resourceName, name, clientData, cancellationToken); }
    public Task DeleteGroupAsync(string resourceName, CancellationToken cancellationToken = default) { Group(resourceName); return inner.DeleteGroupAsync(resourceName, cancellationToken); }
    public Task ModifyMembershipAsync(string groupResourceName, string personResourceName, bool add, CancellationToken cancellationToken = default)
    { Person(personResourceName); if (groupResourceName != "contactGroups/starred") Group(groupResourceName); return inner.ModifyMembershipAsync(groupResourceName, personResourceName, add, cancellationToken); }
}
