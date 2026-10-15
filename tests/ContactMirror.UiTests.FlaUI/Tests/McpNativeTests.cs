using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application.Automation;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Automation;
using ContactMirror.Infrastructure.Demo;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using ModelContextProtocol.Client;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.FlaUI.Tests;

public sealed class McpNativeTests
{
    private static string RepositoryRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName,"ContactMirror.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }
    private static async Task<AgentResult> Call(McpClient client,string tool,JsonObject args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var r = await client.CallToolAsync(tool,args.ToDictionary(p=>p.Key,p=>p.Value is null ? null : (object)JsonSerializer.SerializeToElement(p.Value)),cancellationToken:timeout.Token);
        return JsonSerializer.Deserialize<AgentResult>(r.StructuredContent!.Value.GetRawText(),JsonSemantics.Options)!;
    }
    private static AgentResult Check(AgentResult r) => r.Status == "completed" ? r : throw new InvalidOperationException(r.Error?.Code+": "+r.Error?.Message);
    [Test, NotInParallel("DesktopUi")]
    public async Task Published_MCP_owner_promotes_to_ordinary_window_protects_native_draft_and_refreshes_card()
    {
        var repo = RepositoryRoot(); var app = Environment.GetEnvironmentVariable("CONTACTMIRROR_MCP_TEST_APP") ?? Path.Combine(repo,"artifacts","mcp-validation","app");
        var host = Path.Combine(app,"ContactMirror.exe"); var command = Path.Combine(app,"ContactMirror.Mcp.exe");
        var temp = Path.Combine(Path.GetTempPath(),"ContactMirrorMcpNative",Guid.NewGuid().ToString("N")); var data = Path.Combine(temp,"isolated-data"); var root = Path.Combine(temp,"workspace with spaces"); Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(data,"Automation"));
        await File.WriteAllTextAsync(Path.Combine(data,"Automation","configuration.json"),JsonSerializer.Serialize(new AgentConfiguration(true,true,true,[root],Guid.NewGuid()),JsonSemantics.Options));
        await File.WriteAllTextAsync(Path.Combine(data,"desktop-demo.json"),JsonSerializer.Serialize(new { Folder = root, OAuthPath = "" }));
        var evidence = Path.Combine(repo,"chat-artifacts","mcp","native",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")); Directory.CreateDirectory(evidence);
        Process? owner = null; Window? window = null;
        try
        {
            await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = command,Arguments = ["--host",host],Name = "MCP native acceptance",EnvironmentVariables = new Dictionary<string,string?> { ["CONTACTMIRROR_AUTOMATION_TEST_ROOT"] = data } })))
            {
                _ = await client.ListToolsAsync();
                // The initialized stdio process already locks package files, even before pipe attachment.
                await Assert.That(ContactMirror.Infrastructure.Updates.WindowsUpdateProcesses.HasRunningMcpBridge(app)).IsTrue();
                owner = Process.GetProcessById(Check(await Call(client,"app_get_state",new())).Data!["processId"]!.GetValue<int>());
                Check(await Call(client,"workspace_initialize",new() { ["root"] = root,["expectedAccountKey"] = DemoAccountConnector.Identity.Key,["requestKey"] = Guid.NewGuid().ToString() }));
                var workspace = Check(await Call(client,"workspace_open",new() { ["root"] = root })).Data!["workspace"]!.ToString();
                var c = Check(await Call(client,"contacts_create",new() { ["workspace"] = workspace,["requestKey"] = Guid.NewGuid().ToString(),["data"] = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Анна",["familyName"] = "Примерова" }) } })); var id = c.Data!["id"]!.ToString();
                var launch = new ProcessStartInfo(host) { UseShellExecute = false, WorkingDirectory = app, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }; launch.Environment["CONTACTMIRROR_AUTOMATION_TEST_ROOT"] = data;
                using (var shortcut = Process.Start(launch)!) await shortcut.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                using var application = global::FlaUI.Core.Application.Attach(owner.Id); using var automation = new UIA3Automation();
                window = application.GetMainWindow(automation,TimeSpan.FromSeconds(30)) ?? throw new InvalidOperationException("Ordinary launch did not promote owner.");
                NativeWindowCapture.Resize(window.Properties.NativeWindowHandle.Value,1180,820); window.SetForeground();
                Check(await Call(client,"app_open_contact",new() { ["workspace"] = workspace,["id"] = id }));
                async Task<AutomationElement> Wait(string control)
                {
                    for(var i=0;i<150;i++) { var e = window.FindFirstDescendant(cf=>cf.ByAutomationId(control)); if(e?.IsEnabled==true) return e; await Task.Delay(50); } throw new TimeoutException("Native control: "+control);
                }
                var before = Check(await Call(client,"contacts_get",new() { ["workspace"] = workspace,["id"] = id }));
                await using var video = await NativeWindowVideo.StartAsync(window.Properties.NativeWindowHandle.Value,Path.Combine(evidence,"mcp-draft-and-refresh-after.mp4"));
                (await Wait("ContactEditButton")).AsButton().Invoke(); (await Wait("Editor:data.names.0.givenName")).Patterns.Value.Pattern.SetValue("Несохранённое имя"); await Task.Delay(400);
                var args = new JsonObject { ["workspace"] = workspace,["id"] = id,["expectedVersion"] = before.Data!["version"]!.DeepClone(),["requestKey"] = Guid.NewGuid().ToString(),["patches"] = new JsonArray(new JsonObject { ["op"] = "replace",["path"] = "/data/names/0/givenName",["value"] = "Правка агента" }) };
                var blocked = await Call(client,"contacts_update",args); await Assert.That(blocked.Error!.Code).IsEqualTo("draft_conflict");
                await Assert.That((await Wait("Editor:data.names.0.givenName")).AsTextBox().Text).IsEqualTo("Несохранённое имя");
                NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value,Path.Combine(evidence,"draft-retained.png"));
                (await Wait("ContactCancelButton")).AsButton().Invoke(); args["requestKey"] = Guid.NewGuid().ToString(); Check(await Call(client,"contacts_update",args));
                for(var i=0;i<150;i++) { if(window.FindFirstDescendant(cf=>cf.ByName("Правка агента Примерова")) is not null) break; if(i==149) throw new TimeoutException("MCP card refresh missing."); await Task.Delay(50); }
                NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value,Path.Combine(evidence,"clean-card-refreshed.png"));
                using var bitmap=new SkiaSharp.SKBitmap(32,32);bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);var photo=encoded.ToArray();
                var upload=Check(await Call(client,"photo_upload_begin",new(){["workspace"]=workspace,["uploadKey"]=Guid.NewGuid().ToString(),["length"]=photo.Length,["sha256"]=JsonSemantics.Hash(photo)})).Data!["uploadId"]!.ToString();
                Check(await Call(client,"photo_upload_chunk",new(){["uploadId"]=upload,["offset"]=0,["base64"]=Convert.ToBase64String(photo)}));Check(await Call(client,"photo_upload_complete",new(){["uploadId"]=upload}));
                var withPhoto=Check(await Call(client,"contacts_get",new(){["workspace"]=workspace,["id"]=id}));
                var photoSaved=Check(await Call(client,"contacts_update",new(){["workspace"]=workspace,["id"]=id,["expectedVersion"]=withPhoto.Data!["version"]!.DeepClone(),["requestKey"]=Guid.NewGuid().ToString(),["patches"]=new JsonArray(),["photo"]=new JsonObject{["action"]="set",["uploadId"]=upload}}));
                await Wait("PhotoViewButton");var fetched=Check(await Call(client,"photo_get",new(){["workspace"]=workspace,["id"]=id,["expectedVersion"]=photoSaved.Data!["version"]!.DeepClone(),["count"]=1024}));await Assert.That(fetched.Data!["sha256"]!.ToString()).IsEqualTo(JsonSemantics.Hash(photo));
                NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value,Path.Combine(evidence,"photo-refreshed.png"));
                (await Wait("SettingsButton")).AsButton().Invoke(); await Wait("McpEnabled"); (await Wait("SettingsScroll")).Patterns.Scroll.Pattern.SetScrollPercent(-1,50); await Task.Delay(300); NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value,Path.Combine(evidence,"settings.png"));
                (await Wait("CopyMcpConfigButton")).AsButton().Invoke(); await Task.Delay(150); var configuration=JsonNode.Parse((await Wait("McpConfiguration")).AsTextBox().Text)!;
                await Assert.That(configuration["mcpServers"]!["contactmirror"]!["command"]!.ToString()).IsEqualTo(command); await Assert.That(configuration["mcpServers"]!["contactmirror"]!["args"]![1]!.ToString()).IsEqualTo(host);
                if(Environment.GetEnvironmentVariable("CONTACTMIRROR_MCP_UPDATE_BARRIER")=="1")
                {
                    (await Wait("CheckUpdatesButton")).AsButton().Invoke(); (await Wait("DownloadUpdateButton")).AsButton().Invoke(); (await Wait("RestartUpdateButton")).AsButton().Invoke(); await Task.Delay(500);
                    var error=await Wait("UpdateErrorText"); await Assert.That(error.Name.Contains("MCP",StringComparison.Ordinal)).IsTrue(); await Assert.That(owner.HasExited).IsFalse();
                    (await Wait("SettingsScroll")).Patterns.Scroll.Pattern.SetScrollPercent(-1,100); await Task.Delay(300); NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value,Path.Combine(evidence,"installed-update-blocked.png"));
                }
                await video.FinishAsync();
                await File.WriteAllTextAsync(Path.Combine(evidence,"trace.json"),JsonSerializer.Serialize(new { ordinaryLaunchSameOwner = true,draftProtected = true,cleanCardRefreshed = true,photoRefreshed = true,photoSha256=JsonSemantics.Hash(photo),initializedBridgeBlocksUpdate = true,configurationMatchesInstalledPaths = true,installedUpdateBlocked = Environment.GetEnvironmentVariable("CONTACTMIRROR_MCP_UPDATE_BARRIER")=="1",synthetic = true },JsonSemantics.Options));
                window.Close(); window = null; await Task.Delay(400); await Assert.That(owner.HasExited).IsFalse();
                var ui = Check(await Call(client,"app_get_state",new())).Data!["ui"]!; await Assert.That(ui["hasWindow"]!.GetValue<bool>()).IsFalse();
            }
            if(owner is not null) await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
        }
        finally
        {
            try { window?.Close(); } catch { }
            if(owner is not null) { if(!owner.HasExited) { try { await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); } catch { owner.Kill(); await owner.WaitForExitAsync(); } } owner.Dispose(); }
            if(Directory.Exists(temp)) Directory.Delete(temp,true);
        }
    }
}
