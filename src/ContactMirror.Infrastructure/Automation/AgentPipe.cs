using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContactMirror.Application.Automation;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Configuration;
using ContactMirror.Infrastructure.Updates;

namespace ContactMirror.Infrastructure.Automation;

public static class AgentPipe
{
    public static string Name => ApplicationPaths.AppId + ".Mcp." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsUpdateProcesses.CurrentOwner())))[..24];
    private static readonly string fingerprint = JsonSemantics.Hash(string.Join("|", new[] { typeof(AgentPipe).Assembly, typeof(AgentCall).Assembly, typeof(SyncException).Assembly }.Select(a => a.ManifestModule.ModuleVersionId.ToString())));
    public sealed record Handshake(int Version, string AppId, string Build, string HostPath, string Fingerprint);
    public static Handshake Identity => new(2, ApplicationPaths.AppId, typeof(AgentPipe).Assembly.GetName().Version!.ToString(), Environment.ProcessPath ?? "", fingerprint);
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSemantics.Serialize(value); if (bytes.Length > 32 * 1024 * 1024) throw new SyncException("resource_limit", "Сообщение превышает 32 МБ.");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length); await stream.WriteAsync(prefix, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token); var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is < 1 or > 32 * 1024 * 1024) throw new SyncException("resource_limit", "Недопустимая длина сообщения.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        // Reject duplicates and excessive nesting before deserializing public envelopes.
        _ = JsonSemantics.ParseObject(bytes, "MCP IPC");
        return JsonSerializer.Deserialize<T>(bytes, JsonSemantics.Options) ?? throw new JsonException();
    }
}

public sealed class AgentPipeServer(AgentRuntime runtime) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<Task> connections = [];
    public Task StartAsync() => Task.Run(ListenAsync);
    private async Task ListenAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(AgentPipe.Name, PipeDirection.InOut, 32, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(cancellation.Token); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); return; }
            var task = HandleAsync(pipe); lock (connections) { connections.RemoveAll(t => t.IsCompleted); connections.Add(task); }
        }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        string? bridge = null;
        await using (pipe)
        {
            try
            {
                await AgentPipe.WriteAsync(pipe, AgentPipe.Identity, cancellation.Token);
                bridge = runtime.ConnectBridge();
                while (!cancellation.IsCancellationRequested)
                {
                    var call = await AgentPipe.ReadAsync<AgentCall>(pipe, cancellation.Token);
                    var result = await runtime.CallAsync(call with { BridgeId = bridge }, cancellation.Token);
                    if (AgentWireBudget.Measure(result) > AgentWireBudget.MaximumPayload + 64 * 1024) result = AgentResult.Failure("resource_limit", "Ответ слишком велик; запросите меньшую страницу или порции значения.");
                    await AgentPipe.WriteAsync(pipe, result, cancellation.Token);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or SyncException or UnauthorizedAccessException) { }
            finally { if (bridge is not null) runtime.DisconnectBridge(bridge); }
        }
    }
    public void Dispose() { cancellation.Cancel(); }
}

public sealed class AgentPipeClient(string hostPath) : IAsyncDisposable
{
    private NamedPipeClientStream? pipe;
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<AgentResult> CallAsync(AgentCall call, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) return AgentResult.Failure("busy", "Другой запрос этого клиента ещё выполняется.", true);
        try
        {
            if (pipe is null || !pipe.IsConnected) await ConnectAsync(token);
            await AgentPipe.WriteAsync(pipe!, call, token); return await AgentPipe.ReadAsync<AgentResult>(pipe!, token);
        }
        catch (SyncException e) { return AgentResult.Failure(e.Code, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        { if (pipe is not null) await pipe.DisposeAsync(); pipe = null; return AgentResult.Failure("host_unavailable", "Соединение прервано. Перед повтором записи проверьте requestKey через operations_get."); }
        finally { gate.Release(); }
    }
    private async Task ConnectAsync(CancellationToken token)
    {
        if (!AgentConfiguration.Load().Enabled) throw new SyncException("mcp_disabled", "Включите MCP в настройках ContactMirror.");
        if (File.Exists(ApplicationPaths.ApplyingPath)) throw new SyncException("update_in_progress", "Программа обновляется. Дождитесь завершения.");
        var fullHost = Path.GetFullPath(hostPath);
        if (Path.GetFileName(fullHost) != "ContactMirror.exe" || !File.Exists(fullHost)) throw new SyncException("host_unavailable", "В конфигурации нужен путь к ContactMirror.exe той же сборки.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var started = false;
        while (true)
        {
            var candidate = new NamedPipeClientStream(".", AgentPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await candidate.ConnectAsync(200, timeout.Token);
                var identity = await AgentPipe.ReadAsync<AgentPipe.Handshake>(candidate, timeout.Token);
                if (identity.Version != 2 || identity.AppId != ApplicationPaths.AppId || identity.Build != AgentPipe.Identity.Build || identity.Fingerprint != AgentPipe.Identity.Fingerprint || !Path.GetFullPath(identity.HostPath).Equals(fullHost, StringComparison.OrdinalIgnoreCase))
                    throw new SyncException("upgrade_required", "Открытая программа и MCP принадлежат разным сборкам. Закройте прежний экземпляр и обновите конфигурацию.");
                pipe = candidate; return;
            }
            catch (TimeoutException)
            {
                await candidate.DisposeAsync();
                if (!started)
                {
                    using var process = Process.Start(new ProcessStartInfo(fullHost, "--agent-host") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(fullHost)! }); started = true;
                }
                await Task.Delay(100, timeout.Token);
            }
            catch { await candidate.DisposeAsync(); throw; }
        }
    }
    public async ValueTask DisposeAsync() { if (pipe is not null) await pipe.DisposeAsync(); gate.Dispose(); }
}
