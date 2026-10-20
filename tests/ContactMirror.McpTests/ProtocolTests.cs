using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application.Automation;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Automation;
using ContactMirror.Infrastructure.Demo;
using ModelContextProtocol.Client;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.McpTests;

public sealed partial class RuntimeTests
{
    [Test]
    public async Task Stdio_malformed_JSON_returns_protocol_error_without_non_JSON_stdout()
    {
        var app=Path.Combine(RepositoryRoot(),"artifacts","mcp-validation","app"); var temp=Path.Combine(Path.GetTempPath(),"ContactMirrorMcpMalformed",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var start=new ProcessStartInfo(Path.Combine(app,"ContactMirror.Mcp.exe")) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true }; start.ArgumentList.Add("--host"); start.ArgumentList.Add(Path.Combine(app,"ContactMirror.exe")); start.Environment["CONTACTMIRROR_AUTOMATION_TEST_ROOT"]=temp;
        using var process=Process.Start(start)!;
        try
        {
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"clientInfo\":{\"name\":\"raw-validation\",\"version\":\"1\"},\"capabilities\":{}}}"); await process.StandardInput.FlushAsync();
            var initialized=JsonNode.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!)!; await Assert.That(initialized["result"]).IsNotNull();
            await process.StandardInput.WriteLineAsync("{broken-json"); await process.StandardInput.FlushAsync();
            var error=JsonNode.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!)!; await Assert.That(error["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32700);
            process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await Assert.That(process.ExitCode).IsEqualTo(0);
        }
        finally { process.StandardInput.Close(); if(!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } Directory.Delete(temp,true); }
    }
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ContactMirror.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }
    [Test]
    public async Task Real_stdio_clients_for_legacy_and_current_protocol_share_published_background_owner()
    {
        var app = Path.Combine(RepositoryRoot(), "artifacts", "mcp-validation", "app"); var command = Path.Combine(app, "ContactMirror.Mcp.exe"); var host = Path.Combine(app, "ContactMirror.exe");
        if (!File.Exists(command) || !File.Exists(host)) throw new InvalidOperationException("Publish Synthetic Desktop and MCP payload to artifacts/mcp-validation/app first. This mandatory test is not skipped.");
        var temp = Path.Combine(Path.GetTempPath(), "ContactMirrorMcpProtocol", Guid.NewGuid().ToString("N")); var data = Path.Combine(temp, "isolated-data"); var root = Path.Combine(temp, "workspace with spaces"); Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(data, "Automation"));
        await File.WriteAllTextAsync(Path.Combine(data, "Automation", "configuration.json"), JsonSerializer.Serialize(new AgentConfiguration(true, true, true, [root], Guid.NewGuid()), JsonSemantics.Options));
        await File.WriteAllTextAsync(Path.Combine(data, "desktop-demo.json"), JsonSerializer.Serialize(new { Folder = root, OAuthPath = "" }));
        int? pid = null;
        async Task<McpClient> Connect(string version) => await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = command, Arguments = ["--host", host], Name = "ContactMirror protocol " + version,
            EnvironmentVariables = new Dictionary<string, string?> { ["CONTACTMIRROR_AUTOMATION_TEST_ROOT"] = data }
        }), new() { ProtocolVersion = version, ClientInfo = new() { Name = "ContactMirror.McpTests", Version = "1.0" } });
        static async Task<AgentResult> Call(McpClient client, string name, JsonObject args)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await client.CallToolAsync(name, args.ToDictionary(p => p.Key, p => p.Value is null ? null : (object)JsonSerializer.SerializeToElement(p.Value)), cancellationToken: timeout.Token);
            return JsonSerializer.Deserialize<AgentResult>(result.StructuredContent!.Value.GetRawText(), JsonSemantics.Options)!;
        }
        try
        {
            await using (var legacy = await Connect("2025-11-25"))
            await using (var current = await Connect("2026-07-28"))
            {
                var tools = await legacy.ListToolsAsync(); await Assert.That(tools.Count).IsEqualTo(AgentTools.Names.Length);
                var unknownRejected = false;
                try { _ = await legacy.CallToolAsync("missing_contactmirror_tool", new Dictionary<string,object?>()); }
                catch (ModelContextProtocol.McpProtocolException e) { unknownRejected = e.ErrorCode == ModelContextProtocol.McpErrorCode.InvalidParams; }
                await Assert.That(unknownRejected).IsTrue();
                await Assert.That((await current.ListToolsAsync()).Select(t => t.Name).SequenceEqual(tools.Select(t => t.Name))).IsTrue();
                var state = Check(await Call(legacy, "app_get_state", new())); pid = state.Data!["processId"]!.GetValue<int>();
                Check(await Call(legacy, "workspace_initialize", new() { ["root"] = root, ["requestKey"] = Key(), ["expectedAccountKey"] = DemoAccountConnector.Identity.Key }));
                var opened = Check(await Call(legacy, "workspace_open", new() { ["root"] = root })); var workspace = opened.Data!["workspace"]!.ToString();
                var created = Check(await Call(legacy, "contacts_create", new() { ["workspace"] = workspace, ["requestKey"] = Key(), ["data"] = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Протокольный контакт" }) } }));
                var secondWorkspace = Check(await Call(current, "workspace_open", new() { ["root"] = root })).Data!["workspace"]!.ToString();
                var read = Check(await Call(current, "contacts_get", new() { ["workspace"] = secondWorkspace, ["id"] = created.Data!["id"]!.DeepClone() }));
                await Assert.That(read.Data!["document"]!["data"]!["names"]![0]!["givenName"]!.ToString()).IsEqualTo("Протокольный контакт");
                await Assert.That(Check(await Call(current, "app_get_state", new())).Data!["processId"]!.GetValue<int>()).IsEqualTo(pid.Value);
                Check(await Call(current, "contacts_update", new() { ["workspace"] = secondWorkspace, ["id"] = created.Data!["id"]!.DeepClone(), ["expectedVersion"] = read.Data!["version"]!.DeepClone(), ["requestKey"] = Key(), ["patches"] = Patch("/starred", JsonValue.Create(true)) }));
                await Assert.That(Check(await Call(legacy, "contacts_get", new() { ["workspace"] = workspace, ["id"] = created.Data!["id"]!.DeepClone() })).Data!["document"]!["starred"]!.GetValue<bool>()).IsTrue();
                for(var i=0;i<24;i++) Check(await Call(current,"contacts_create",new() { ["workspace"] = secondWorkspace,["requestKey"] = Key(),["data"] = new JsonObject { ["biographies"] = new JsonArray(new JsonObject { ["value"] = new string('Я',30000) }) } }));
                var large=Check(await Call(current,"contacts_list",new() { ["workspace"] = secondWorkspace,["take"] = 200,["filters"] = new JsonArray(new JsonObject { ["columnId"] = "data.biographies.value",["operator"] = "HasValue" }),["projection"] = new JsonArray("data.biographies.value") }));
                await Assert.That(large.Data!["items"]!.AsArray().Count).IsGreaterThan(0); await Assert.That(large.Data["nextCursor"]).IsNotNull(); await Assert.That(AgentWireBudget.Measure(large)<4*1024*1024).IsTrue();
                var trace = Path.Combine(RepositoryRoot(), "chat-artifacts", "mcp"); Directory.CreateDirectory(trace);
                await File.WriteAllTextAsync(Path.Combine(trace, "stdio-protocol-validation.json"), JsonSerializer.Serialize(new { protocols = new[] { "2025-11-25", "2026-07-28" }, tools = tools.Select(t => t.Name), sharedOwner = true, createReadUpdate = true, synthetic = true }, JsonSemantics.Options));
            }
        }
        finally
        {
            if (pid is int owner)
            {
                try { using var process = Process.GetProcessById(owner); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)); await process.WaitForExitAsync(timeout.Token); }
                catch (ArgumentException) { }
            }
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }
}
