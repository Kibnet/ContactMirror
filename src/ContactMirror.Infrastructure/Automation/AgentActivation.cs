using System.IO.Pipes;
using ContactMirror.Application.Automation;

namespace ContactMirror.Infrastructure.Automation;

/// <summary>Window activation is available even when data access through MCP is disabled.</summary>
public sealed class AgentActivation(Func<Task> show) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    public Task StartAsync() => Task.Run(async () =>
    {
        while (!cancellation.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(AgentPipe.Name + ".Window", PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(cancellation.Token); if (await AgentPipe.ReadAsync<stringRequest>(pipe, cancellation.Token) is { Action: "show" }) await show(); await AgentPipe.WriteAsync(pipe, new { shown = true }, cancellation.Token); }
            catch (Exception e) when (e is IOException or OperationCanceledException or System.Text.Json.JsonException) { }
        }
    });
    public static async Task<bool> ShowExistingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(".", AgentPipe.Name + ".Window", PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(timeout.Token); await AgentPipe.WriteAsync(pipe, new stringRequest("show"), timeout.Token); _ = await AgentPipe.ReadAsync<System.Text.Json.Nodes.JsonObject>(pipe, timeout.Token); return true; }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { return false; }
    }
    private sealed record stringRequest(string Action);
    public void Dispose() => cancellation.Cancel();
}
