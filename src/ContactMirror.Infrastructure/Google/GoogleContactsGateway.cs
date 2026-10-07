using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ContactMirror.Application;
using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Google;

/// <summary>Raw People API boundary. Mutations are never retried; the coordinator owns their journal.</summary>
public sealed class GoogleContactsGateway(IAccessTokenProvider tokenProvider, HttpClient? client = null, Func<TimeSpan, CancellationToken, Task>? retryDelay = null) : IGoogleContactsGateway
{
    public const string PersonFields = "addresses,ageRanges,biographies,birthdays,calendarUrls,clientData,coverPhotos,emailAddresses,events,externalIds,genders,imClients,interests,locales,locations,memberships,metadata,miscKeywords,names,nicknames,occupations,organizations,phoneNumbers,photos,relations,sipAddresses,skills,urls,userDefined";
    public const string GroupFields = "clientData,groupType,memberCount,metadata,name";
    private static readonly HashSet<string> WritableFields = new("addresses,biographies,birthdays,calendarUrls,clientData,emailAddresses,events,externalIds,genders,imClients,interests,locales,locations,miscKeywords,names,nicknames,occupations,organizations,phoneNumbers,relations,sipAddresses,urls,userDefined,memberships".Split(','), StringComparer.Ordinal);
    private readonly HttpClient http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly Func<TimeSpan, CancellationToken, Task> wait = retryDelay ?? ((delay, token) => Task.Delay(delay, token));
    private static string Q(string value) => Uri.EscapeDataString(value);
    private static string ReadQuery => $"personFields={Q(PersonFields)}&sources=READ_SOURCE_TYPE_CONTACT&sources=READ_SOURCE_TYPE_PROFILE";

    public async Task<RemoteSnapshot> ReadAllAsync(IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var people = await ReadPagesAsync("people/me/connections?" + ReadQuery + "&pageSize=1000", "connections", progress, cancellationToken);
        var groups = await ReadPagesAsync("contactGroups?groupFields=" + Q(GroupFields) + "&pageSize=1000", "contactGroups", progress, cancellationToken);
        var truncated = people.Where(p => (p["phoneNumbers"] as JsonArray)?.Count >= 100 || (p["emailAddresses"] as JsonArray)?.Count >= 100).ToList();
        foreach (var batch in truncated.Chunk(200))
        {
            var resources = batch.Select(p => RequiredResource(p, "people")).ToHashSet(StringComparer.Ordinal);
            var response = await SendAsync(HttpMethod.Get, "people:batchGet?" + ReadQuery + "&" + string.Join('&', resources.Select(r => "resourceNames=" + Q(r))), null, false, cancellationToken, progress: progress);
            if (response?["responses"] is not JsonArray results) throw Incomplete();
            var replacements = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var item in results)
            {
                if (item is not JsonObject result || result["status"] is JsonObject status && status["code"]?.GetValue<int>() is int code && code != 0)
                    throw Incomplete();
                if (result["person"] is not JsonObject person) throw Incomplete();
                var resource = RequiredResource(person, "people");
                if (!resources.Contains(resource) || !replacements.TryAdd(resource, (JsonObject)person.DeepClone())) throw Incomplete();
            }
            if (replacements.Count != resources.Count) throw Incomplete();
            foreach (var original in batch) people[people.IndexOf(original)] = replacements[RequiredResource(original, "people")];
        }
        progress?.Report(new SyncProgress("Данные Google прочитаны полностью", people.Count, people.Count));
        return new RemoteSnapshot(people, groups);
    }

    private async Task<List<JsonObject>> ReadPagesAsync(string path, string collection, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var items = new List<JsonObject>();
        var resources = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        int? expectedCount = null;
        do
        {
            var response = await SendAsync(HttpMethod.Get, path + (page is null ? "" : "&pageToken=" + Q(page)), null, false, ct, progress: progress) ?? throw Incomplete();
            if (response["totalItems"] is JsonValue total)
            {
                var count = total.GetValue<int>();
                if (count < 0 || expectedCount.HasValue && expectedCount != count) throw Incomplete();
                expectedCount = count;
            }
            if (response[collection] is JsonArray array)
            {
                foreach (var node in array)
                {
                    if (node is not JsonObject item) throw Incomplete();
                    var resource = RequiredResource(item, collection == "connections" ? "people" : "contactGroups");
                    if (!resources.Add(resource) || item["metadata"]?["deleted"]?.GetValue<bool>() == true) throw Incomplete();
                    items.Add((JsonObject)item.DeepClone());
                }
            }
            else if (response.ContainsKey(collection)) throw Incomplete();
            page = response["nextPageToken"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(page) && !tokens.Add(page)) throw Incomplete();
            progress?.Report(new SyncProgress(collection == "connections" ? "Чтение контактов Google" : "Чтение ярлыков Google", items.Count, expectedCount ?? 0));
        } while (!string.IsNullOrEmpty(page));
        if (expectedCount.HasValue && expectedCount != items.Count) throw Incomplete();
        return items;
    }

    public Task<JsonObject?> GetPersonAsync(string resourceName, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, Resource(resourceName, "people") + "?" + ReadQuery, null, false, cancellationToken, allowNotFound: true);

    public async Task<JsonObject> CreateContactAsync(JsonObject person, CancellationToken cancellationToken = default)
        => RequireResourceResult(await SendAsync(HttpMethod.Post, "people:createContact?" + ReadQuery, person, true, cancellationToken), "people");

    public async Task<JsonObject> UpdateContactAsync(string resourceName, JsonObject person, IReadOnlyCollection<string> fields, CancellationToken cancellationToken = default)
    {
        var resource = Resource(resourceName, "people");
        if (fields.Count == 0 || fields.Any(f => !WritableFields.Contains(f))) throw new SyncException("invalid-field-mask", "В запросе есть неподдерживаемое поле для записи.");
        return RequireResourceResult(await SendAsync(HttpMethod.Patch, resource + ":updateContact?" + ReadQuery + "&updatePersonFields=" + Q(string.Join(',', fields.Distinct().Order(StringComparer.Ordinal))), person, true, cancellationToken), "people", resource);
    }

    public async Task DeleteContactAsync(string resourceName, CancellationToken cancellationToken = default)
        => _ = await SendAsync(HttpMethod.Delete, Resource(resourceName, "people") + ":deleteContact", null, true, cancellationToken);

    public async Task<JsonObject> UpdatePhotoAsync(string resourceName, byte[]? bytes, CancellationToken cancellationToken = default)
    {
        var resource = Resource(resourceName, "people");
        JsonObject? body = bytes is null ? null : new JsonObject { ["photoBytes"] = Convert.ToBase64String(bytes), ["personFields"] = PersonFields, ["sources"] = new JsonArray("READ_SOURCE_TYPE_CONTACT", "READ_SOURCE_TYPE_PROFILE") };
        var path = bytes is null ? resource + ":deleteContactPhoto?" + ReadQuery : resource + ":updateContactPhoto";
        var result = await SendAsync(bytes is null ? HttpMethod.Delete : HttpMethod.Patch, path, body, true, cancellationToken);
        if (result?["person"] is not JsonObject person) throw new SyncException("photo-result-incomplete", "Google принял запрос фото, но не вернул полную запись. Требуется проверка результата.", true);
        return RequireResourceResult((JsonObject)person.DeepClone(), "people", resource);
    }

    public async Task<byte[]> DownloadPhotoAsync(string url, CancellationToken cancellationToken = default)
    {
        var uri = ValidatePhotoUri(url);
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.RequestMessage?.RequestUri is Uri finalUri) ValidatePhotoUri(finalUri.AbsoluteUri);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var location = response.Headers.Location;
                if (location is null || redirect == 3) throw new SyncException("photo-redirect", "Не удалось безопасно скачать фото Google.");
                uri = ValidatePhotoUri(new Uri(uri, location).AbsoluteUri);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new SyncException("photo-download", $"Google не вернул фото (HTTP {(int)response.StatusCode}).");
            const int maxBytes = 20 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maxBytes || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                throw new SyncException("photo-invalid", "Google вернул неподдерживаемое изображение.");
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (output.Length + read > maxBytes) throw new SyncException("photo-size", "Размер фото превышает 20 МБ.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        throw new SyncException("photo-redirect", "Не удалось скачать фото Google.");
    }

    public static Uri ValidatePhotoUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0
            || !(IsHost(uri.IdnHost, "googleusercontent.com") || IsHost(uri.IdnHost, "ggpht.com")))
            throw new SyncException("photo-host", "Адрес фото не принадлежит поддерживаемому HTTPS-серверу Google.");
        return uri;
    }
    private static bool IsHost(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    public Task<JsonObject?> GetGroupAsync(string resourceName, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, Resource(resourceName, "contactGroups") + "?groupFields=" + Q(GroupFields) + "&maxMembers=0", null, false, cancellationToken, allowNotFound: true);

    public async Task<JsonObject> CreateGroupAsync(string name, JsonArray clientData, CancellationToken cancellationToken = default)
        => RequireResourceResult(await SendAsync(HttpMethod.Post, "contactGroups", new JsonObject { ["contactGroup"] = new JsonObject { ["name"] = name, ["clientData"] = clientData.DeepClone() }, ["readGroupFields"] = GroupFields }, true, cancellationToken), "contactGroups");

    public async Task<JsonObject> UpdateGroupAsync(string resourceName, string name, JsonArray clientData, CancellationToken cancellationToken = default)
    {
        var resource = Resource(resourceName, "contactGroups");
        return RequireResourceResult(await SendAsync(HttpMethod.Put, resource, new JsonObject { ["contactGroup"] = new JsonObject { ["resourceName"] = resource, ["name"] = name, ["clientData"] = clientData.DeepClone() }, ["updateGroupFields"] = "name,clientData", ["readGroupFields"] = GroupFields }, true, cancellationToken), "contactGroups", resource);
    }
    public async Task DeleteGroupAsync(string resourceName, CancellationToken cancellationToken = default)
        => _ = await SendAsync(HttpMethod.Delete, Resource(resourceName, "contactGroups") + "?deleteContacts=false", null, true, cancellationToken);

    public async Task ModifyMembershipAsync(string groupResourceName, string personResourceName, bool add, CancellationToken cancellationToken = default)
    {
        var group = Resource(groupResourceName, "contactGroups");
        var person = Resource(personResourceName, "people");
        var response = await SendAsync(HttpMethod.Post, group + "/members:modify", new JsonObject { [add ? "resourceNamesToAdd" : "resourceNamesToRemove"] = new JsonArray(person) }, true, cancellationToken);
        if (response is null || response["notFoundResourceNames"] is JsonArray missing && missing.Count > 0 || response["canNotRemoveLastContactGroupResourceNames"] is JsonArray last && last.Count > 0)
            throw new SyncException("membership-failed", "Google не подтвердил изменение ярлыка для контакта. Требуется проверка результата.", true);
    }

    public static string Resource(string value, string prefix)
    {
        if (!Regex.IsMatch(value, "\\A" + Regex.Escape(prefix) + "/[A-Za-z0-9_-]+\\z", RegexOptions.CultureInvariant) || value == "people/me")
            throw new SyncException("invalid-resource", "Неверный идентификатор записи Google.");
        return value;
    }
    private static string RequiredResource(JsonObject item, string prefix) => Resource(item["resourceName"]?.GetValue<string>() ?? "", prefix);
    private static SyncException Incomplete() => new("google-incomplete", "Google вернул неполные или несогласованные данные. Синхронизация остановлена; повторите проверку.");
    private static JsonObject RequireResourceResult(JsonObject? value, string prefix, string? expected = null)
    {
        try
        {
            if (value is null) throw new JsonException();
            var actual = RequiredResource(value, prefix);
            if (expected is not null && expected != actual) throw new JsonException();
            return value;
        }
        catch (Exception ex) when (ex is SyncException or JsonException or InvalidOperationException)
        { throw new SyncException("google-result-incomplete", "Google не вернул подтверждённый идентификатор результата записи. Требуется проверка состояния.", true, ex); }
    }

    public static TimeSpan ReadRetryDelay(HttpResponseMessage response, int attempt, DateTimeOffset now)
    {
        var requested = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - now : (TimeSpan?)null);
        var backoff = response.StatusCode == HttpStatusCode.TooManyRequests
            ? TimeSpan.FromSeconds(Math.Min(60, 15 * Math.Pow(2, attempt)) + Random.Shared.NextDouble())
            : TimeSpan.FromMilliseconds(250 * (attempt + 1));
        return requested is { } delay && delay > backoff ? delay : backoff;
    }

    private async Task<JsonObject?> SendAsync(HttpMethod method, string path, JsonObject? body, bool mutation, CancellationToken ct, bool allowNotFound = false, IProgress<SyncProgress>? progress = null)
    {
        for (var attempt = 0; attempt < (mutation ? 1 : 6); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var token = await tokenProvider.GetAccessTokenAsync(ct);
            using var request = new HttpRequestMessage(method, "https://people.googleapis.com/v1/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            try
            {
                using var response = await http.SendAsync(request, ct);
                if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode)
                {
                    if (!mutation && (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 5 || (int)response.StatusCode >= 500 && attempt < 2))
                    {
                        var delay = ReadRetryDelay(response, attempt, DateTimeOffset.UtcNow);
                        if (delay > TimeSpan.FromMinutes(5)) throw await GoogleErrors.ReadAsync(response, false, false, ct);
                        progress?.Report(new($"Google просит паузу. Повторим чтение через {Math.Ceiling(delay.TotalSeconds)} с… Можно отменить операцию."));
                        await wait(delay, ct);
                        continue;
                    }
                    throw await GoogleErrors.ReadAsync(response, false, mutation, ct);
                }
                var content = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(content)) return null;
                return JsonNode.Parse(content) as JsonObject ?? throw new JsonException();
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                if (mutation) throw new SyncException("google-outcome-unknown", "Запрос отправлен, но ответ не подтверждён. Требуется проверка состояния Google.", true, ex);
                if (ct.IsCancellationRequested) throw;
                if (ex is not JsonException && attempt < 2) { await Task.Delay(250 * (attempt + 1), ct); continue; }
                throw new SyncException("google-read-failed", "Не удалось полностью прочитать Google. Изменения не применены.", false, ex);
            }
        }
        throw Incomplete();
    }
}
