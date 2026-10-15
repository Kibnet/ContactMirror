using System.Reflection;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Automation;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Automation;
using ContactMirror.Infrastructure.Demo;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.McpTests;

public sealed partial class RuntimeTests
{
    [Test]
    public async Task Completion_wins_over_cancel_captured_while_running()
    {
        foreach(var terminal in new[]{"completed","failed","unknown"})
        {
            var lifecycle=new AgentJobLifecycle();var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateCancel=Task.Run(async()=>{await Assert.That(lifecycle.State).IsEqualTo("running");observed.SetResult();await release.Task;return lifecycle.RequestCancel();});
            await observed.Task;lifecycle.Complete(terminal);release.SetResult();await Assert.That(await lateCancel).IsFalse();await Assert.That(lifecycle.State).IsEqualTo(terminal);
            var earlierCancel=new AgentJobLifecycle();await Assert.That(earlierCancel.RequestCancel()).IsTrue();earlierCancel.Complete(terminal);await Assert.That(earlierCancel.RequestCancel()).IsFalse();await Assert.That(earlierCancel.State).IsEqualTo(terminal);
        }
    }
    public class GatewayProbe : DispatchProxy
    {
        public IGoogleContactsGateway Inner = null!;
        public bool BlockRead, LoseCreateReply;
        public int Creates;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method!.Name==nameof(IGoogleContactsGateway.ReadAllAsync) && BlockRead) return Read((CancellationToken)args![1]!);
            if(method.Name==nameof(IGoogleContactsGateway.CreateContactAsync)) return Create((JsonObject)args![0]!, (CancellationToken)args[1]!);
            return method.Invoke(Inner,args);
        }
        private async Task<RemoteSnapshot> Read(CancellationToken token) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite,token); throw new InvalidOperationException(); }
        private async Task<JsonObject> Create(JsonObject p,CancellationToken token) { Creates++; var result = await Inner.CreateContactAsync(p,token); if(LoseCreateReply) throw new SyncException("remote_timeout","Ответ потерян после записи.",true); return result; }
    }
    private static GatewayProbe Probe(Fixture f)
    {
        var gateway = DispatchProxy.Create<IGoogleContactsGateway,GatewayProbe>(); var probe = (GatewayProbe)gateway; probe.Inner=f.Gateway;
        f.Runtime.Reconfigure(f.Account,new SyncCoordinator(new GuardedGoogleGateway(gateway),f.Store)); return probe;
    }
    [Test]
    public async Task Own_cancel_and_EOF_stop_unissued_prepare_other_bridge_cannot_cancel()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var probe = Probe(f); probe.BlockRead=true; var bridge=f.Runtime.ConnectBridge(); await f.OpenOn(bridge);
        var started = await f.Call("sync_prepare",new() { ["workspace"] = f.Workspace,["expectedAccountKey"] = DemoAccountConnector.Identity.Key },bridge); await Assert.That(started.Status).IsEqualTo("accepted"); await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var other=f.Runtime.ConnectBridge(); var rejected=await f.Call("operations_cancel",new() { ["operationId"] = started.OperationId },other); await Assert.That(rejected.Error!.Code).IsEqualTo("operation_not_owned");
        Check(await f.Call("operations_cancel",new() { ["operationId"] = started.OperationId },bridge)); var done=await f.FinishOn(started,bridge); await Assert.That(done.Error!.Code).IsEqualTo("cancelled");
        Check(await f.Call("operations_cancel",new() { ["operationId"] = started.OperationId },bridge));
        probe.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously); var next=await f.Call("sync_prepare",new() { ["workspace"] = f.Workspace,["expectedAccountKey"] = DemoAccountConnector.Identity.Key },bridge); await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); f.Runtime.DisconnectBridge(bridge);
        for(var i=0;f.Runtime.HasRunningJobs && i<100;i++) await Task.Delay(20); await Assert.That(f.Runtime.HasRunningJobs).IsFalse(); await Assert.That(probe.Creates).IsEqualTo(0); f.Runtime.DisconnectBridge(other);
    }
    [Test]
    public async Task Lost_remote_create_reply_is_unknown_and_same_key_never_issues_duplicate()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); _=await f.Create("После таймаута"); var probe=Probe(f); probe.LoseCreateReply=true; var bridge=f.Runtime.ConnectBridge(); await f.OpenOn(bridge);
        var prepared=Check(await f.FinishOn(await f.Call("sync_prepare",new() { ["workspace"] = f.Workspace,["expectedAccountKey"] = DemoAccountConnector.Identity.Key },bridge),bridge));
        var plan=prepared.Data!["planId"]!.ToString(); var read=Check(await f.Call("sync_get_plan",new() { ["planId"] = plan,["take"] = 200 },bridge));
        var choices=new JsonArray(read.Data!["items"]!.AsArray().Where(n=>n!["defaultSelected"]!.GetValue<bool>()).Select(n=>(JsonNode)new JsonObject { ["entryKey"] = n!["entryKey"]!.DeepClone(),["resolution"] = "Automatic" }).ToArray());
        var reviewed=Check(await f.Call("sync_get_plan",new() { ["planId"] = plan,["choices"] = choices.DeepClone() },bridge)); var args=new JsonObject { ["planId"] = plan,["choices"] = choices,["selectionDigest"] = reviewed.Data!["selectionDigest"]!.DeepClone(),["requestKey"] = Key() };
        var first=await f.FinishOn(await f.Call("sync_apply",args,bridge),bridge); await Assert.That(first.Status).IsEqualTo("unknown");
        var replay=await f.Call("sync_apply",args,bridge); await Assert.That(replay.Status).IsEqualTo("unknown"); await Assert.That(probe.Creates).IsEqualTo(1);
        var remote=await f.Gateway.ReadAllAsync(); await Assert.That(remote.People.Count(p=>p["names"]?[0]?["givenName"]?.ToString()=="После таймаута")).IsEqualTo(1); f.Runtime.DisconnectBridge(bridge);
    }
    private sealed class BlockRefreshUi : IAgentUi
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AgentUiState> GetStateAsync()=>Task.FromResult(new AgentUiState());
        public Task ShowWindowAsync()=>Task.CompletedTask;
        public Task OpenContactAsync(string root,Guid id)=>Task.CompletedTask;
        public async Task ChangedAsync(string? root) { Entered.TrySetResult(); await Release.Task; }
    }
    [Test]
    public async Task Account_operation_status_remains_readable_between_signin_and_UI_refresh()
    {
        await using var f = new Fixture(); await f.Account.SignOutAsync(); var ui=new BlockRefreshUi(); f.Runtime.Ui=ui;
        var key=Key();var started=await f.Call("account_connect",new() { ["interactive"] = true,["expectedAccountKey"] = null,["requestKey"] = key }); await ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { var status=Check(await f.Call("operations_get",new() { ["operationId"] = started.OperationId })); await Assert.That(status.Data!["state"]!.ToString()).IsEqualTo("running");var reconnect=f.Runtime.ConnectBridge();try {var byKey=await f.Call("operations_get",new(){["requestKey"]=key.ToUpperInvariant()},reconnect);await Assert.That(byKey.Status).IsEqualTo("running");}finally{f.Runtime.DisconnectBridge(reconnect);} }
        finally { ui.Release.TrySetResult(); }
        Check(await f.Finish(started));
    }
    [Test]
    public async Task Numeric_enum_and_oversized_filter_are_rejected_without_query_normalization()
    {
        await using var f=new Fixture(); await f.InitializeAsync();
        foreach(var operand in new[] { ("0","A"),("Equal",new string('A',10001)) })
        {
            var result=await f.Call("contacts_list",new() { ["workspace"] = f.Workspace,["filters"] = new JsonArray(new JsonObject { ["columnId"] = "displayName",["operator"] = operand.Item1,["value"] = operand.Item2 }) }); await Assert.That(result.Error!.Code).IsEqualTo("invalid_filter");
        }
    }
}
