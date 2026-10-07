using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Google;
using Xunit;

namespace ContactMirror.GoogleTests;

public sealed class GoogleGatewayTests
{
    private sealed class Tokens : IAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("synthetic-token");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request); return action(request); }
    }
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static GoogleContactsGateway Gateway(Handler handler) => new(new Tokens(), new HttpClient(handler));

    [Fact]
    public async Task FullScanFollowsBothCatalogsAndPreservesUnknownSources()
    {
        var handler = new Handler(request =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (uri.Contains("/connections"))
            {
                Assert.Contains("personFields=" + GoogleContactsGateway.PersonFields, uri);
                Assert.Contains("sources=READ_SOURCE_TYPE_CONTACT&sources=READ_SOURCE_TYPE_PROFILE", uri);
                return Task.FromResult(uri.Contains("pageToken=next") ? Json("{\"connections\":[{\"resourceName\":\"people/c2\"}],\"totalItems\":2}") : Json("{\"connections\":[{\"resourceName\":\"people/c1\",\"unknown\":{\"a\":1},\"names\":[{\"metadata\":{\"source\":{\"type\":\"PROFILE\"}}}]}],\"nextPageToken\":\"next\",\"totalItems\":2}"));
            }
            Assert.Contains("groupFields=" + GoogleContactsGateway.GroupFields, uri);
            return Task.FromResult(uri.Contains("pageToken=next") ? Json("{\"contactGroups\":[{\"resourceName\":\"contactGroups/g2\"}],\"totalItems\":2}") : Json("{\"contactGroups\":[{\"resourceName\":\"contactGroups/g1\",\"clientData\":[{\"key\":\"a\",\"value\":\"b\"}]}],\"nextPageToken\":\"next\",\"totalItems\":2}"));
        });
        var snapshot = await Gateway(handler).ReadAllAsync();
        Assert.Equal(2, snapshot.People.Count);
        Assert.Equal(2, snapshot.Groups.Count);
        Assert.Equal(1, snapshot.People[0]["unknown"]!["a"]!.GetValue<int>());
        Assert.Equal("PROFILE", snapshot.People[0]["names"]![0]!["metadata"]!["source"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"connections\":[],\"totalItems\":1}")]
    [InlineData("{\"connections\":[{\"resourceName\":\"people/c1\"},{\"resourceName\":\"people/c1\"}]}")]
    [InlineData("{\"connections\":[{\"resourceName\":\"people/c1\",\"metadata\":{\"deleted\":true}}]}")]
    [InlineData("{\"connections\":{}}")]
    public async Task IncompleteCatalogIsNeverReturned(string response)
    {
        var handler = new Handler(_ => Task.FromResult(Json(response)));
        Assert.Equal("google-incomplete", (await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).ReadAllAsync())).Code);
    }

    [Fact]
    public async Task APageFailureFailsWholeScan()
    {
        var count = 0;
        var handler = new Handler(_ => Task.FromResult(++count == 1 ? Json("{\"connections\":[{\"resourceName\":\"people/c1\"}],\"nextPageToken\":\"next\"}") : Json("{}", HttpStatusCode.BadRequest)));
        await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).ReadAllAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("phoneNumbers")]
    [InlineData("emailAddresses")]
    public async Task HundredValuesHydratesCompletePerson(string field)
    {
        var truncated = new JsonObject { ["resourceName"] = "people/c1", [field] = new JsonArray(Enumerable.Range(0, 100).Select(i => (JsonNode)new JsonObject { ["value"] = "value" + i }).ToArray()) };
        var complete = (JsonObject)truncated.DeepClone();
        ((JsonArray)complete[field]!).Add(new JsonObject { ["value"] = "extra" });
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("batchGet"))
            {
                Assert.Contains("resourceNames=people%2Fc1", request.RequestUri.Query);
                return Task.FromResult(Json(new JsonObject { ["responses"] = new JsonArray(new JsonObject { ["person"] = complete.DeepClone(), ["status"] = new JsonObject { ["code"] = 0 } }) }.ToJsonString()));
            }
            return Task.FromResult(Json(request.RequestUri.AbsolutePath.Contains("connections") ? new JsonObject { ["connections"] = new JsonArray(truncated.DeepClone()) }.ToJsonString() : "{}"));
        });
        var result = await Gateway(handler).ReadAllAsync();
        Assert.Equal(101, ((JsonArray)result.People.Single()[field]!).Count);
    }

    [Theory]
    [InlineData("{\"responses\":[]}")]
    [InlineData("{\"responses\":[{\"status\":{\"code\":404}}]}")]
    [InlineData("{\"responses\":[{\"person\":{\"resourceName\":\"people/c2\"}}]}")]
    public async Task BatchGetMissingOrFailedPersonBlocksCompleteness(string batchResponse)
    {
        var person = new JsonObject { ["resourceName"] = "people/c1", ["phoneNumbers"] = new JsonArray(Enumerable.Range(0, 100).Select(_ => (JsonNode)new JsonObject()).ToArray()) };
        var handler = new Handler(request => Task.FromResult(Json(request.RequestUri!.AbsolutePath.Contains("batchGet") ? batchResponse : request.RequestUri.AbsolutePath.Contains("connections") ? new JsonObject { ["connections"] = new JsonArray(person.DeepClone()) }.ToJsonString() : "{}")));
        await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).ReadAllAsync());
    }

    [Theory]
    [InlineData("people/c1?x=y")]
    [InlineData("people/c1/../../contactGroups/g1")]
    [InlineData("people/%63x")]
    [InlineData("https://evil.test/a")]
    [InlineData("people/me")]
    public async Task ResourceInjectionRejectedBeforeRequest(string resource)
    {
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("Network must not be reached"));
        await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).GetPersonAsync(resource));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UpdateUsesExactSelectedMaskAndPreservesCallerMetadata()
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Contains("updatePersonFields=names%2CphoneNumbers", request.RequestUri!.Query);
            var person = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal("contact-etag", person["metadata"]!["sources"]![0]!["etag"]!.GetValue<string>());
            return Json("{\"resourceName\":\"people/c1\"}");
        });
        await Gateway(handler).UpdateContactAsync("people/c1", JsonNode.Parse("{\"metadata\":{\"sources\":[{\"type\":\"CONTACT\",\"etag\":\"contact-etag\"}]},\"names\":[]}")!.AsObject(), ["phoneNumbers", "names"]);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("skills")]
    [InlineData("photos")]
    [InlineData("names&delete=true")]
    public async Task UnsupportedWriteMaskRejected(string field)
    {
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("Network must not be reached"));
        await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).UpdateContactAsync("people/c1", new(), [field]));
    }

    [Theory]
    [InlineData("{\"notFoundResourceNames\":[\"people/c1\"]}")]
    [InlineData("{\"canNotRemoveLastContactGroupResourceNames\":[\"people/c1\"]}")]
    public async Task MembershipHttpSuccessWithResourceFailureIsNotSuccess(string result)
    {
        var handler = new Handler(_ => Task.FromResult(Json(result)));
        var error = await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).ModifyMembershipAsync("contactGroups/g1", "people/c1", false));
        Assert.True(error.IsAmbiguous);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GroupDeleteNeverDeletesContacts()
    {
        var handler = new Handler(request => { Assert.Equal("?deleteContacts=false", request.RequestUri!.Query); return Task.FromResult(Json("{}")); });
        await Gateway(handler).DeleteGroupAsync("contactGroups/g1");
    }

    [Fact]
    public async Task GroupUpdateReplacesNameAndClientDataAndReadsFullMask()
    {
        var handler = new Handler(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("name,clientData", body["updateGroupFields"]!.GetValue<string>());
            Assert.Equal(GoogleContactsGateway.GroupFields, body["readGroupFields"]!.GetValue<string>());
            Assert.Equal("b", body["contactGroup"]!["clientData"]![0]!["value"]!.GetValue<string>());
            return Json("{\"resourceName\":\"contactGroups/g1\"}");
        });
        await Gateway(handler).UpdateGroupAsync("contactGroups/g1", "Label", new JsonArray(new JsonObject { ["key"] = "a", ["value"] = "b" }));
    }

    [Fact]
    public async Task AmbiguousCreateNeverRetries()
    {
        var handler = new Handler(_ => throw new HttpRequestException("Synthetic disconnect after dispatch"));
        var error = await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).CreateContactAsync(new()));
        Assert.True(error.IsAmbiguous);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"resourceName\":\"people/c1?spoof=true\"}")]
    [InlineData("[]")]
    public async Task InvalidSuccessfulCreateCannotBeAcknowledged(string result)
    {
        var handler = new Handler(_ => Task.FromResult(Json(result)));
        var error = await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).CreateContactAsync(new()));
        Assert.True(error.IsAmbiguous);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MutationHttp500IsAmbiguousWithoutRetry()
    {
        var handler = new Handler(_ => Task.FromResult(Json("{}", HttpStatusCode.InternalServerError)));
        var error = await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).CreateContactAsync(new()));
        Assert.True(error.IsAmbiguous);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RetryOnlyReadsAndBoundAttempts()
    {
        var handler = new Handler(_ => Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).GetPersonAsync("people/c1"));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task RateLimitWaitsForRetryAfterAndThenCompletesRead()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var handler = new Handler(_ =>
        {
            if (++calls > 1) return Task.FromResult(Json("{\"resourceName\":\"people/c1\"}"));
            var limited = Json("{}", HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return Task.FromResult(limited);
        });
        var gateway = new GoogleContactsGateway(new Tokens(), new HttpClient(handler), (delay, token) => { delays.Add(delay); return Task.CompletedTask; });
        Assert.NotNull(await gateway.GetPersonAsync("people/c1"));
        Assert.Equal(TimeSpan.FromSeconds(90), Assert.Single(delays));
        Assert.Equal(2, calls);
    }

    private sealed class RotatingTokens : IAccessTokenProvider
    {
        public string Token { get; set; } = "before-pause";
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(Token);
    }

    [Fact]
    public async Task ReadRefreshesAccessTokenAfterLongQuotaPause()
    {
        var tokens = new RotatingTokens();
        var headers = new List<string?>();
        var handler = new Handler(request =>
        {
            headers.Add(request.Headers.Authorization?.Parameter);
            var response = Json("{}", headers.Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        });
        var gateway = new GoogleContactsGateway(tokens, new HttpClient(handler), (_, _) => { tokens.Token = "after-pause"; return Task.CompletedTask; });
        await gateway.GetPersonAsync("people/c1");
        Assert.Equal(new[] { "before-pause", "after-pause" }, headers);
    }

    [Fact]
    public async Task PersistentRateLimitIsBoundedAndDoesNotAskForLogin()
    {
        var handler = new Handler(_ => Task.FromResult(Json("{}", HttpStatusCode.TooManyRequests)));
        var delays = new List<TimeSpan>();
        var gateway = new GoogleContactsGateway(new Tokens(), new HttpClient(handler), (delay, token) => { delays.Add(delay); return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<SyncException>(() => gateway.GetPersonAsync("people/c1"));
        Assert.Equal("google-rate-limit", error.Code);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(5, delays.Count);
        Assert.InRange(delays[0].TotalSeconds, 15, 16);
        Assert.InRange(delays[1].TotalSeconds, 30, 31);
        Assert.InRange(delays[4].TotalSeconds, 60, 61);
        Assert.False(error.IsAmbiguous);
    }

    [Fact]
    public async Task CancellationDuringQuotaPauseStopsWithoutAnotherRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler(_ => Task.FromResult(Json("{}", HttpStatusCode.TooManyRequests)));
        var gateway = new GoogleContactsGateway(new Tokens(), new HttpClient(handler), (_, token) => { cancellation.Cancel(); return Task.FromCanceled(token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.GetPersonAsync("people/c1", cancellation.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RateLimitedMutationIsNeverAutomaticallyRepeated()
    {
        var handler = new Handler(_ => Task.FromResult(Json("{}", HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).CreateContactAsync(new()));
        Assert.Equal("google-rate-limit", error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NotFoundTargetIsNull()
    {
        var handler = new Handler(_ => Task.FromResult(Json("{}", HttpStatusCode.NotFound)));
        Assert.Null(await Gateway(handler).GetPersonAsync("people/c1"));
    }

    [Fact]
    public async Task PhotoUploadIsBase64AndReturnsInnerPerson()
    {
        var handler = new Handler(async request =>
        {
            Assert.EndsWith(":updateContactPhoto", request.RequestUri!.AbsolutePath);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal("AQID", body["photoBytes"]!.GetValue<string>());
            Assert.Equal(GoogleContactsGateway.PersonFields, body["personFields"]!.GetValue<string>());
            return Json("{\"person\":{\"resourceName\":\"people/c1\"}}");
        });
        Assert.Equal("people/c1", (await Gateway(handler).UpdatePhotoAsync("people/c1", [1, 2, 3]))["resourceName"]!.GetValue<string>());
    }

    [Fact]
    public async Task PhotoDeleteUsesDeleteAndFullReadMask()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.EndsWith(":deleteContactPhoto", request.RequestUri!.AbsolutePath);
            Assert.Contains("personFields=", request.RequestUri.Query);
            return Task.FromResult(Json("{\"person\":{\"resourceName\":\"people/c1\"}}"));
        });
        await Gateway(handler).UpdatePhotoAsync("people/c1", null);
    }

    [Theory]
    [InlineData("https://evil.test/photo")]
    [InlineData("http://lh3.googleusercontent.com/photo")]
    [InlineData("https://googleusercontent.com.evil.test/photo")]
    [InlineData("https://lh3.googleusercontent.com:8443/photo")]
    [InlineData("https://user@lh3.googleusercontent.com/photo")]
    public async Task UnsafePhotoHostNeverReached(string url)
    {
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("Network must not be reached"));
        await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).DownloadPhotoAsync(url));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PhotoRedirectOffGoogleRejectedAndNeverForwardsBearer()
    {
        var handler = new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            var response = Json("", HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.test/photo");
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<SyncException>(() => Gateway(handler).DownloadPhotoAsync("https://lh3.googleusercontent.com/photo"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PhotoSafeRedirectDownloadsImageWithoutToken()
    {
        var handler = new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            if (request.RequestUri!.AbsolutePath == "/a")
            {
                var redirect = Json("", HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://lh4.googleusercontent.com/b");
                return Task.FromResult(redirect);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        });
        Assert.Equal(new byte[] { 1, 2, 3 }, await Gateway(handler).DownloadPhotoAsync("https://lh3.googleusercontent.com/a"));
    }
}
