using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Google;
using ContactMirror.Infrastructure.Configuration;

namespace ContactMirror.Infrastructure.Demo;

/// <summary>A synthetic, durable local provider. No method performs a network request.</summary>
public sealed class DemoGoogleGateway(string? statePath = null) : IGoogleContactsGateway
{
    private readonly string statePath = statePath ?? Path.Combine(ApplicationPaths.DataRoot, "Demo", "google-state.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    private JsonObject? state;
    private JsonArray People => state!["people"]!.AsArray();
    private JsonArray Groups => state!["groups"]!.AsArray();
    private JsonObject Photos => state!["photos"]!.AsObject();
    private static JsonObject Copy(JsonObject value) => (JsonObject)value.DeepClone();
    private static readonly byte[] SamplePhoto = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public async Task<RemoteSnapshot> ReadAllAsync(IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            Recount();
            progress?.Report(new SyncProgress("Демонстрационные данные прочитаны", People.Count, People.Count));
            return new(People.OfType<JsonObject>().Select(Copy).ToList(), Groups.OfType<JsonObject>().Select(Copy).ToList());
        }
        finally { gate.Release(); }
    }
    public Task<JsonObject?> GetPersonAsync(string resourceName, CancellationToken cancellationToken = default)
        => ReadAsync("people", resourceName, "people", cancellationToken);
    public Task<JsonObject?> GetGroupAsync(string resourceName, CancellationToken cancellationToken = default)
        => ReadAsync("groups", resourceName, "contactGroups", cancellationToken);
    private async Task<JsonObject?> ReadAsync(string collection, string resource, string prefix, CancellationToken ct)
    {
        GoogleContactsGateway.Resource(resource, prefix);
        await gate.WaitAsync(ct);
        try { await LoadAsync(ct); Recount(); var item = Find(state![collection]!.AsArray(), resource); return item is null ? null : Copy(item); }
        finally { gate.Release(); }
    }

    public Task<JsonObject> CreateContactAsync(JsonObject person, CancellationToken cancellationToken = default)
        => MutateAsync(() =>
        {
            var created = Copy(person);
            created["resourceName"] = "people/demo" + NextId();
            created["metadata"] = new JsonObject { ["sources"] = new JsonArray(new JsonObject { ["type"] = "CONTACT", ["id"] = created["resourceName"]!.GetValue<string>().Split('/')[1] }) };
            foreach (var field in CapabilityRegistry.WritableFields) if (created[field] is JsonArray fields) Tag(fields, created);
            if (!created.ContainsKey("memberships")) created["memberships"] = Memberships("contactGroups/myContacts");
            if (created["memberships"] is not JsonArray)
                throw new SyncException("demo-memberships", "Ярлыки демонстрационного контакта должны быть массивом.");
            Tag(created["memberships"]!.AsArray(), created);
            Touch(created);
            People.Add(created);
            return Copy(created);
        }, cancellationToken);

    public Task<JsonObject> UpdateContactAsync(string resourceName, JsonObject person, IReadOnlyCollection<string> fields, CancellationToken cancellationToken = default)
        => MutateAsync(() =>
        {
            GoogleContactsGateway.Resource(resourceName, "people");
            var existing = Required(People, resourceName);
            if (fields.Count == 0 || fields.Any(f => !CapabilityRegistry.WritableFields.Contains(f) && f != "memberships")) throw new SyncException("demo-mask", "Неподдерживаемое поле демонстрационного контакта.");
            if (person["metadata"]?["sources"] is JsonArray sources)
            {
                var supplied = sources.OfType<JsonObject>().FirstOrDefault(x => x["type"]?.GetValue<string>() == "CONTACT");
                if (supplied?["etag"]?.GetValue<string>() != PersonCodec.Source(existing)["etag"]?.GetValue<string>()) throw new SyncException("google-http-400", "Контакт изменился после проверки. Проверьте изменения повторно.");
            }
            foreach (var field in fields)
            {
                var combined = new JsonArray((existing[field] as JsonArray)?.Where(v => !PersonCodec.IsContactValue(v, existing)).Select(v => v?.DeepClone()).ToArray() ?? []);
                if (person[field] is JsonArray newValues)
                {
                    var edited = (JsonArray)newValues.DeepClone();
                    Tag(edited, existing);
                    foreach (var item in edited) combined.Add(item?.DeepClone());
                }
                if (combined.Count > 0) existing[field] = combined; else existing.Remove(field);
            }
            Touch(existing);
            return Copy(existing);
        }, cancellationToken);

    public async Task DeleteContactAsync(string resourceName, CancellationToken cancellationToken = default)
        => _ = await MutateAsync(() => { GoogleContactsGateway.Resource(resourceName, "people"); People.Remove(Required(People, resourceName)); return new JsonObject(); }, cancellationToken);

    public Task<JsonObject> UpdatePhotoAsync(string resourceName, byte[]? bytes, CancellationToken cancellationToken = default)
        => MutateAsync(() =>
        {
            GoogleContactsGateway.Resource(resourceName, "people");
            var person = Required(People, resourceName);
            var photos = new JsonArray((person["photos"] as JsonArray)?.Where(v => !PersonCodec.IsContactValue(v, person)).Select(v => v?.DeepClone()).ToArray() ?? []);
            if (bytes is not null)
            {
                var url = "https://lh3.googleusercontent.com/contactmirror-demo/" + Convert.ToHexString(SHA256.HashData(bytes)) + ".png";
                Photos[url] = Convert.ToBase64String(bytes);
                var contactPhotos = new JsonArray(new JsonObject { ["url"] = url, ["default"] = false });
                Tag(contactPhotos, person);
                photos.Add(contactPhotos[0]!.DeepClone());
            }
            if (photos.Count > 0) person["photos"] = photos; else person.Remove("photos");
            Touch(person);
            return Copy(person);
        }, cancellationToken);
    public async Task<byte[]> DownloadPhotoAsync(string url, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            return Photos[url]?.GetValue<string>() is string bytes ? Convert.FromBase64String(bytes) : throw new SyncException("demo-photo", "В демонстрации нет этого изображения.");
        }
        finally { gate.Release(); }
    }

    public Task<JsonObject> CreateGroupAsync(string name, JsonArray clientData, CancellationToken cancellationToken = default)
        => MutateAsync(() =>
        {
            CheckUniqueName(name);
            var group = new JsonObject { ["resourceName"] = "contactGroups/demo" + NextId(), ["name"] = name, ["groupType"] = "USER_CONTACT_GROUP", ["clientData"] = clientData.DeepClone() };
            Touch(group);
            Groups.Add(group);
            return Copy(group);
        }, cancellationToken);
    public Task<JsonObject> UpdateGroupAsync(string resourceName, string name, JsonArray clientData, CancellationToken cancellationToken = default)
        => MutateAsync(() =>
        {
            GoogleContactsGateway.Resource(resourceName, "contactGroups");
            var group = Required(Groups, resourceName);
            RequireUserGroup(group);
            CheckUniqueName(name, resourceName);
            group["name"] = name;
            group["clientData"] = clientData.DeepClone();
            Touch(group);
            return Copy(group);
        }, cancellationToken);
    public async Task DeleteGroupAsync(string resourceName, CancellationToken cancellationToken = default)
        => _ = await MutateAsync(() =>
        {
            GoogleContactsGateway.Resource(resourceName, "contactGroups");
            var group = Required(Groups, resourceName);
            RequireUserGroup(group);
            Groups.Remove(group);
            foreach (var person in People.OfType<JsonObject>())
            {
                if (person["memberships"] is not JsonArray memberships) continue;
                var old = memberships.Where(m => MembershipResource(m) == resourceName).ToList();
                foreach (var item in old) memberships.Remove(item);
                if (old.Count > 0) Touch(person);
            }
            return new JsonObject();
        }, cancellationToken);
    public async Task ModifyMembershipAsync(string groupResourceName, string personResourceName, bool add, CancellationToken cancellationToken = default)
        => _ = await MutateAsync(() =>
        {
            GoogleContactsGateway.Resource(groupResourceName, "contactGroups");
            GoogleContactsGateway.Resource(personResourceName, "people");
            _ = Required(Groups, groupResourceName);
            var person = Required(People, personResourceName);
            var memberships = person["memberships"] as JsonArray ?? new JsonArray();
            var matches = memberships.Where(m => MembershipResource(m) == groupResourceName).ToList();
            if (add && matches.Count == 0)
            {
                var value = Memberships(groupResourceName);
                Tag(value, person);
                memberships.Add(value[0]!.DeepClone());
            }
            if (!add)
            {
                if (matches.Count > 0 && memberships.Count == matches.Count) throw new SyncException("membership-failed", "Контакт должен остаться хотя бы в одном ярлыке.");
                foreach (var item in matches) memberships.Remove(item);
            }
            person["memberships"] = memberships;
            Touch(person);
            return new JsonObject();
        }, cancellationToken);

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { state = Seed(); await SaveAsync(cancellationToken); }
        finally { gate.Release(); }
    }
    private async Task<JsonObject> MutateAsync(Func<JsonObject> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            var original = Copy(state!);
            try { var result = action(); Recount(); await SaveAsync(ct); return result; }
            catch { state = original; throw; }
        }
        finally { gate.Release(); }
    }
    private async Task LoadAsync(CancellationToken ct)
    {
        if (state is not null) return;
        if (!File.Exists(statePath)) { state = Seed(); await SaveAsync(ct); return; }
        try
        {
            state = JsonNode.Parse(await File.ReadAllTextAsync(statePath, ct))!.AsObject();
            if (state["schemaVersion"]?.GetValue<int>() != 1 || state["people"] is not JsonArray || state["groups"] is not JsonArray || state["photos"] is not JsonObject)
                throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        { state = null; throw new SyncException("demo-state", "Файл демонстрации повреждён. Сбросьте демонстрационные данные.", false, ex); }
    }
    private async Task SaveAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        var temp = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, state!.ToJsonString(new() { WriteIndented = true }), ct); File.Move(temp, statePath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private int NextId() { var next = state!["nextId"]!.GetValue<int>(); state["nextId"] = next + 1; return next; }
    private void Touch(JsonObject item)
    {
        var version = state!["revision"]!.GetValue<int>() + 1;
        state["revision"] = version;
        item["etag"] = "demo-etag-" + version;
        if (item["metadata"]?["sources"] is JsonArray sources)
            foreach (var source in sources.OfType<JsonObject>().Where(s => s["type"]?.GetValue<string>() == "CONTACT")) source["etag"] = "demo-contact-etag-" + version;
    }
    private void Recount()
    {
        foreach (var group in Groups.OfType<JsonObject>()) group["memberCount"] = People.OfType<JsonObject>().Count(p => (p["memberships"] as JsonArray)?.Any(m => MembershipResource(m) == group["resourceName"]?.GetValue<string>()) == true);
    }
    private static JsonObject? Find(JsonArray collection, string resource) => collection.OfType<JsonObject>().FirstOrDefault(x => x["resourceName"]?.GetValue<string>() == resource);
    private static JsonObject Required(JsonArray collection, string resource) => Find(collection, resource) ?? throw new SyncException("google-http-404", "Запись демонстрации больше не существует.");
    private void CheckUniqueName(string name, string? except = null)
    {
        if (string.IsNullOrWhiteSpace(name) || Groups.OfType<JsonObject>().Any(g => g["name"]?.GetValue<string>() == name && g["resourceName"]?.GetValue<string>() != except)) throw new SyncException("google-http-409", "Имя ярлыка должно быть непустым и уникальным.");
    }
    private static void RequireUserGroup(JsonObject group) { if (group["groupType"]?.GetValue<string>() != "USER_CONTACT_GROUP") throw new SyncException("demo-system-group", "Системные ярлыки нельзя переименовать или удалить."); }
    private static string? MembershipResource(JsonNode? value) => value?["contactGroupMembership"]?["contactGroupResourceName"]?.GetValue<string>();
    private static JsonArray Memberships(params string[] resources) => new(resources.Select(r => (JsonNode)new JsonObject { ["contactGroupMembership"] = new JsonObject { ["contactGroupResourceName"] = r } }).ToArray());
    private static void Tag(JsonArray fields, JsonObject person)
    {
        var source = PersonCodec.Source(person);
        foreach (var item in fields.OfType<JsonObject>())
        {
            var metadata = item["metadata"] as JsonObject ?? new();
            metadata["source"] = new JsonObject { ["type"] = "CONTACT", ["id"] = source["id"]!.DeepClone() };
            item["metadata"] = metadata;
        }
    }
    private static JsonObject Seed()
    {
        var people = new JsonArray();
        var records = new[]
        {
            ("Александра", "Ветрова", "alexandra", "Дизайнер", "Коллеги"),
            ("Михаил", "Луговой", "mikhail", "Разработчик", "Коллеги"),
            ("Елена", "Искрина", "elena", "Фотограф", "Друзья"),
            ("Даниил", "Речной", "daniil", "Переводчик", "Друзья"),
            ("София", "Листова", "sofia", "Редактор", "Коллеги"),
            ("Лев", "Облачный", "lev", "Музыкант", "Друзья")
        };
        for (var index = 0; index < records.Length; index++)
        {
            var (first, last, alias, occupation, label) = records[index];
            var id = "demo" + (index + 1);
            var person = new JsonObject
            {
                ["resourceName"] = "people/" + id, ["etag"] = "demo-etag-initial-" + index,
                ["metadata"] = new JsonObject { ["sources"] = new JsonArray(new JsonObject { ["type"] = "CONTACT", ["id"] = id, ["etag"] = "demo-contact-etag-initial-" + index }) },
                ["names"] = new JsonArray(new JsonObject { ["givenName"] = first, ["familyName"] = last, ["displayName"] = first + " " + last }),
                ["emailAddresses"] = new JsonArray(new JsonObject { ["value"] = alias + "@example.test", ["type"] = "work" }),
                ["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = "+1 202 555 01" + index.ToString("00"), ["type"] = "mobile" }),
                ["occupations"] = new JsonArray(new JsonObject { ["value"] = occupation }),
                ["biographies"] = new JsonArray(new JsonObject { ["value"] = "Вымышленный контакт для знакомства с ContactMirror.", ["contentType"] = "TEXT_PLAIN" }),
                ["userDefined"] = new JsonArray(new JsonObject { ["key"] = "Как познакомились", ["value"] = index % 2 == 0 ? "На творческой встрече" : "На вымышленной конференции" }),
                ["memberships"] = Memberships("contactGroups/myContacts", label == "Коллеги" ? "contactGroups/demo-colleagues" : "contactGroups/demo-friends")
            };
            if (index is 0 or 2) person["memberships"]!.AsArray().Add(Memberships("contactGroups/starred")[0]!.DeepClone());
            if (index == 0)
            {
                person["birthdays"] = new JsonArray(new JsonObject { ["date"] = new JsonObject { ["year"] = 0, ["month"] = 5, ["day"] = 21 } });
                person["organizations"] = new JsonArray(new JsonObject { ["name"] = "Студия «Тёплый свет»", ["title"] = "Дизайнер интерфейсов", ["type"] = "work" });
                person["urls"] = new JsonArray(new JsonObject { ["value"] = "https://portfolio.example.test", ["type"] = "portfolio" });
                person["photos"] = new JsonArray(new JsonObject { ["url"] = "https://lh3.googleusercontent.com/contactmirror-demo/sample.png", ["default"] = false });
            }
            if (index == 1)
            {
                person["sipAddresses"] = new JsonArray(new JsonObject { ["value"] = "sip:mikhail@example.test", ["type"] = "work" });
                person["clientData"] = new JsonArray(new JsonObject { ["key"] = "demo-workflow", ["value"] = "onboarding" });
                person["externalIds"] = new JsonArray(new JsonObject { ["value"] = "DEMO-002", ["type"] = "account" });
                person["events"] = new JsonArray(new JsonObject { ["date"] = new JsonObject { ["year"] = 0, ["month"] = 9, ["day"] = 12 }, ["type"] = "anniversary" });
            }
            if (index == 3)
            {
                person["locales"] = new JsonArray(new JsonObject { ["value"] = "ru-RU" }, new JsonObject { ["value"] = "en-US" });
                person["interests"] = new JsonArray(new JsonObject { ["value"] = "Походы" });
                person["calendarUrls"] = new JsonArray(new JsonObject { ["url"] = "https://calendar.example.test/daniil", ["type"] = "work" });
            }
            foreach (var entry in person.Where(p => p.Value is JsonArray).ToList()) Tag(entry.Value!.AsArray(), person);
            if (index == 4)
            {
                person["metadata"]!["sources"]!.AsArray().Add(new JsonObject { ["type"] = "PROFILE", ["id"] = "demo-profile" });
                person["emailAddresses"]!.AsArray().Add(new JsonObject { ["value"] = "profile-only@example.test", ["type"] = "other", ["metadata"] = new JsonObject { ["source"] = new JsonObject { ["type"] = "PROFILE", ["id"] = "demo-profile" } } });
                person["skills"] = new JsonArray(new JsonObject { ["value"] = "Редактирование", ["metadata"] = new JsonObject { ["source"] = new JsonObject { ["type"] = "PROFILE", ["id"] = "demo-profile" } } });
            }
            people.Add(person);
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1, ["nextId"] = 10, ["revision"] = 0, ["people"] = people,
            ["groups"] = new JsonArray(
                new JsonObject { ["resourceName"] = "contactGroups/myContacts", ["name"] = "Мои контакты", ["groupType"] = "SYSTEM_CONTACT_GROUP", ["etag"] = "demo-system-contacts" },
                new JsonObject { ["resourceName"] = "contactGroups/starred", ["name"] = "Избранное", ["groupType"] = "SYSTEM_CONTACT_GROUP", ["etag"] = "demo-system-starred" },
                new JsonObject { ["resourceName"] = "contactGroups/demo-colleagues", ["name"] = "Коллеги", ["groupType"] = "USER_CONTACT_GROUP", ["etag"] = "demo-colleagues", ["clientData"] = new JsonArray(new JsonObject { ["key"] = "color", ["value"] = "lavender" }) },
                new JsonObject { ["resourceName"] = "contactGroups/demo-friends", ["name"] = "Друзья", ["groupType"] = "USER_CONTACT_GROUP", ["etag"] = "demo-friends", ["clientData"] = new JsonArray() }),
            ["photos"] = new JsonObject { ["https://lh3.googleusercontent.com/contactmirror-demo/sample.png"] = Convert.ToBase64String(SamplePhoto) }
        };
    }
}
