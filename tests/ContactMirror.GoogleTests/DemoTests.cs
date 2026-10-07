using System.Text.Json.Nodes;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Demo;
using Xunit;

namespace ContactMirror.GoogleTests;

public sealed class DemoTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ContactMirror-DemoTests-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(folder, "demo-state.json");
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

    [Fact]
    public async Task DemoHasIsolatedAccountAndSyntheticRichData()
    {
        var connector = new DemoAccountConnector();
        var account = await connector.GetAccountAsync();
        Assert.Equal("contactmirror://demo|contactmirror-demo", account!.Key);
        Assert.Contains("вымышленные контакты", connector.ConfigurationHint);
        var remote = await new DemoGoogleGateway(StatePath).ReadAllAsync();
        Assert.Equal(6, remote.People.Count);
        Assert.Equal(2, remote.Groups.Count(g => g["groupType"]!.GetValue<string>() == "USER_CONTACT_GROUP"));
        Assert.All(remote.People, p => Assert.Single(p["metadata"]!["sources"]!.AsArray(), s => s!["type"]!.GetValue<string>() == "CONTACT"));
        Assert.Equal(0, remote.People[0]["birthdays"]![0]!["date"]!["year"]!.GetValue<int>());
        Assert.Single(remote.People[1]["sipAddresses"]!.AsArray());
        var profileContact = remote.People[4];
        Assert.Equal(2, profileContact["emailAddresses"]!.AsArray().Count);
        Assert.Single(PersonCodec.ContactValues(profileContact, "emailAddresses"));
        foreach (var person in remote.People)
            foreach (var (field, value) in PersonCodec.Data(person)) Assert.Empty(CapabilityRegistry.ValidateEditable(field, value));
        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public async Task DemoFieldReplacementPreservesProfileAndPersistsAcrossRestart()
    {
        var first = new DemoGoogleGateway(StatePath);
        var original = (await first.ReadAllAsync()).People[4];
        var request = new JsonObject { ["metadata"] = original["metadata"]!.DeepClone(), ["emailAddresses"] = new JsonArray(new JsonObject { ["value"] = "edited@example.test", ["type"] = "home" }) };
        var updated = await first.UpdateContactAsync("people/demo5", request, ["emailAddresses"]);
        Assert.NotEqual(PersonCodec.Source(original)["etag"]!.GetValue<string>(), PersonCodec.Source(updated)["etag"]!.GetValue<string>());
        Assert.Equal(2, updated["emailAddresses"]!.AsArray().Count);
        Assert.Equal("edited@example.test", PersonCodec.ContactValues(updated, "emailAddresses")[0]!["value"]!.GetValue<string>());
        Assert.Contains(updated["emailAddresses"]!.AsArray(), x => x!["value"]!.GetValue<string>() == "profile-only@example.test");
        var restarted = new DemoGoogleGateway(StatePath);
        Assert.Equal(updated.ToJsonString(), (await restarted.GetPersonAsync("people/demo5"))!.ToJsonString());
    }

    [Fact]
    public async Task DemoRejectsStaleEtagAndDoesNotPersistTheRejectedEdit()
    {
        var gateway = new DemoGoogleGateway(StatePath);
        var original = (await gateway.ReadAllAsync()).People[0];
        var request = new JsonObject { ["metadata"] = original["metadata"]!.DeepClone(), ["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = "+1 202 555 0199" }) };
        await gateway.UpdateContactAsync("people/demo1", request, ["phoneNumbers"]);
        request["phoneNumbers"] = new JsonArray();
        await Assert.ThrowsAsync<SyncException>(() => gateway.UpdateContactAsync("people/demo1", request, ["phoneNumbers"]));
        Assert.Single((await new DemoGoogleGateway(StatePath).GetPersonAsync("people/demo1"))!["phoneNumbers"]!.AsArray());
    }

    [Fact]
    public async Task DemoGroupDeletionPreservesEveryContactAndCleansMemberships()
    {
        var gateway = new DemoGoogleGateway(StatePath);
        var before = await gateway.ReadAllAsync();
        await gateway.DeleteGroupAsync("contactGroups/demo-colleagues");
        var after = await gateway.ReadAllAsync();
        Assert.Equal(before.People.Count, after.People.Count);
        Assert.Null(await gateway.GetGroupAsync("contactGroups/demo-colleagues"));
        Assert.All(after.People, p => Assert.DoesNotContain("contactGroups/demo-colleagues", PersonCodec.Memberships(p)));
    }

    [Fact]
    public async Task DemoCreateGroupClientDataMembershipAndPhotoHaveFullSemantics()
    {
        var gateway = new DemoGoogleGateway(StatePath);
        var group = await gateway.CreateGroupAsync("Новый ярлык", new JsonArray(new JsonObject { ["key"] = "a", ["value"] = "b" }));
        var groupResource = group["resourceName"]!.GetValue<string>();
        var person = await gateway.CreateContactAsync(new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Новый" }) });
        var resource = PersonCodec.Resource(person);
        await gateway.ModifyMembershipAsync(groupResource, resource, true);
        Assert.Contains(groupResource, PersonCodec.Memberships((await gateway.GetPersonAsync(resource))!));
        Assert.Equal(1, (await gateway.GetGroupAsync(groupResource))!["memberCount"]!.GetValue<int>());
        await gateway.ModifyMembershipAsync(groupResource, resource, false);
        Assert.DoesNotContain(groupResource, PersonCodec.Memberships((await gateway.GetPersonAsync(resource))!));
        await gateway.UpdateGroupAsync(groupResource, "Переименован", new JsonArray());
        Assert.Empty((await gateway.GetGroupAsync(groupResource))!["clientData"]!.AsArray());
        var withPhoto = await gateway.UpdatePhotoAsync(resource, [1, 2, 3]);
        var url = PersonCodec.PhotoUrl(withPhoto)!;
        Assert.Equal(new byte[] { 1, 2, 3 }, await new DemoGoogleGateway(StatePath).DownloadPhotoAsync(url));
        var withoutPhoto = await gateway.UpdatePhotoAsync(resource, null);
        Assert.Null(PersonCodec.PhotoUrl(withoutPhoto));
        await gateway.DeleteContactAsync(resource);
        Assert.Null(await gateway.GetPersonAsync(resource));
    }

    [Fact]
    public async Task DemoCreatePreservesExplicitLabelsAndStarredAcrossRestart()
    {
        var gateway = new DemoGoogleGateway(StatePath);
        var supplied = new JsonArray(
            new JsonObject { ["contactGroupMembership"] = new JsonObject { ["contactGroupResourceName"] = "contactGroups/demo-friends" } },
            new JsonObject { ["contactGroupMembership"] = new JsonObject { ["contactGroupResourceName"] = "contactGroups/starred" } });
        var created = await gateway.CreateContactAsync(new JsonObject
        {
            ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Новый" }),
            ["memberships"] = supplied.DeepClone()
        });
        Assert.Equal(new[] { "contactGroups/demo-friends", "contactGroups/starred" }, PersonCodec.Memberships(created));
        Assert.DoesNotContain("contactGroups/myContacts", PersonCodec.Memberships(created));
        var persisted = await new DemoGoogleGateway(StatePath).GetPersonAsync(PersonCodec.Resource(created));
        Assert.Equal(PersonCodec.Memberships(created), PersonCodec.Memberships(persisted!));
        Assert.All(created["memberships"]!.AsArray(), membership => Assert.True(PersonCodec.IsContactValue(membership, created)));
        Assert.All(supplied, membership => Assert.Null(membership!["metadata"]));
    }

    [Fact]
    public async Task DemoCreateDefaultsMembershipOnlyWhenPropertyIsMissing()
    {
        var gateway = new DemoGoogleGateway(StatePath);
        var defaulted = await gateway.CreateContactAsync(new JsonObject());
        Assert.Equal(new[] { "contactGroups/myContacts" }, PersonCodec.Memberships(defaulted));
        var explicitEmpty = await gateway.CreateContactAsync(new JsonObject { ["memberships"] = new JsonArray() });
        Assert.Empty(PersonCodec.Memberships(explicitEmpty));
    }

    [Fact]
    public async Task DemoCopiesReturnedObjectsAndNeverFetchesUnknownUrl()
    {
        var gateway = new DemoGoogleGateway(StatePath);
        var remote = await gateway.ReadAllAsync();
        remote.People[0]["names"] = new JsonArray();
        Assert.NotEmpty((await gateway.GetPersonAsync("people/demo1"))!["names"]!.AsArray());
        await Assert.ThrowsAsync<SyncException>(() => gateway.DownloadPhotoAsync("https://evil.test/photo"));
    }

    [Fact]
    public async Task DemoResetAffectsOnlySyntheticStateAndDisconnectIsExplicit()
    {
        var gateway = new DemoGoogleGateway(StatePath);
        await gateway.DeleteContactAsync("people/demo1");
        await gateway.ResetAsync();
        Assert.Equal(6, (await gateway.ReadAllAsync()).People.Count);
        var connector = new DemoAccountConnector();
        await connector.SignOutAsync(revoke: true);
        Assert.Null(await connector.GetAccountAsync());
        Assert.Equal(DemoAccountConnector.Identity, await connector.SignInAsync());
    }
}
