using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContactMirror.Infrastructure.Automation;
using ContactMirror.Core;

namespace ContactMirror.Desktop;

public sealed partial class MainWindowViewModel
{
    public AgentRuntime? Automation { get; private set; }
    public bool HasMcp => Automation is not null;
    public bool McpRestartAllowed => Automation?.ConnectedClients is not > 0 && !ContactMirror.Infrastructure.Updates.WindowsUpdateProcesses.HasRunningMcpBridge(AppContext.BaseDirectory);
    public string McpStatus => Automation is null ? "" : $"Подключено клиентов: {Automation.ConnectedClients}" + (!McpRestartAllowed ? ". Перед обновлением завершите MCP-клиенты." : "");
    [ObservableProperty] private bool mcpEnabled;
    [ObservableProperty] private bool mcpLocalWrite = true;
    [ObservableProperty] private bool mcpGoogleWrite;
    [ObservableProperty] private string mcpRoots = "";
    [ObservableProperty] private string mcpConfiguration = "";
    private bool agentRefreshRequested;
    private bool agentRefreshing;
    private bool automationDetached;
    private long agentRefreshRevision;
    private long coveredAgentRefreshRevision;
    public void AttachAutomation(AgentRuntime runtime)
    {
        Automation = runtime; var c = AgentConfiguration.Load();
        McpEnabled = c.Enabled; McpLocalWrite = c.LocalWrite; McpGoogleWrite = c.GoogleWrite; McpRoots = string.Join(Environment.NewLine, c.AllowedRoots);
        var prior = Contacts.Guard.IsTransitionBlocked; Contacts.Guard.IsTransitionBlocked = () => !McpRestartAllowed || prior?.Invoke() == true;
        var priorReason = Contacts.Guard.TransitionBlockedReason;
        Contacts.Guard.TransitionBlockedReason = () => !McpRestartAllowed ? "Перед обновлением завершите MCP-клиенты ContactMirror." : priorReason?.Invoke();
        runtime.Changed += AutomationChanged; _activity.Changed += AutomationChanged;
        OnPropertyChanged(nameof(HasMcp)); AutomationChanged();
    }
    private void AutomationChanged() => Dispatcher.UIThread.Post(async () =>
    {
        if (automationDetached || _closeRequested) return;
        OnPropertyChanged(nameof(McpStatus)); OnPropertyChanged(nameof(McpRestartAllowed));
        if (!_activity.IsActive && !LocalEditorOpen && agentRefreshRequested && !agentRefreshing)
        {
            agentRefreshing = true;
            agentRefreshRequested = false;
            var revision = Interlocked.Read(ref agentRefreshRevision);
            try
            {
                await ReloadConnectionAsync(CancellationToken.None);
                if (automationDetached || _closeRequested) return;
                if (revision <= coveredAgentRefreshRevision) return;
                if (LocalEditorOpen || _activity.IsActive) { agentRefreshRequested = true; return; }
                InvalidatePreview();
                if (!Contacts.IsDirty && !Contacts.HasModal && FolderReady)
                {
                    var id = Contacts.Original?.Id; await Contacts.LoadAsync(Folder);
                    if (id is Guid selected && Contacts.Rows.Any(r => r.Id == selected)) await Contacts.SelectAsync(selected);
                }
            }
            catch (Exception e) when (e is SyncException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            { if (!automationDetached) { Error = "Не удалось обновить отображение после операции агента. Проверьте папку и подключение."; Status = "Сохранённые данные не перезаписаны. Обновите список вручную."; } }
            finally { agentRefreshing = false; if (agentRefreshRequested) AutomationChanged(); }
        }
    });
    public void RequestAgentRefresh(string? root)
    {
        var revision = Interlocked.Increment(ref agentRefreshRevision);
        Dispatcher.UIThread.Post(() =>
        {
            if (revision <= coveredAgentRefreshRevision) return;
            if (root is null || string.Equals(root, Folder, StringComparison.OrdinalIgnoreCase)) agentRefreshRequested = true;
            AutomationChanged();
        });
    }
    private void AcknowledgeAgentRefresh() { coveredAgentRefreshRevision = Interlocked.Read(ref agentRefreshRevision); agentRefreshRequested = false; }
    [RelayCommand] private void AddMcpFolder()
    {
        if (!CanConfigure || Contacts.IsDirty || !FolderReady) return;
        var roots = McpRoots.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Append(Folder).Distinct(StringComparer.OrdinalIgnoreCase);
        McpRoots = string.Join(Environment.NewLine, roots);
    }
    [RelayCommand] private void SaveMcpSettings()
    {
        if (!CanConfigure || Contacts.IsDirty || Contacts.HasModal) { Status = "Сначала завершите операцию и сохраните черновик."; return; }
        try
        {
            _ = new AgentConfiguration(McpEnabled, McpLocalWrite, McpGoogleWrite, McpRoots.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Save();
            Automation?.Revoke(); McpConfiguration = BuildMcpConfiguration(); Status = "Настройки MCP сохранены. Клиентам нужно подключиться заново.";
        }
        catch (Exception e) when (e is SyncException or IOException or UnauthorizedAccessException or ArgumentException) { Error = e.Message; }
    }
    [RelayCommand] private void DisableMcp()
    {
        try { _ = (AgentConfiguration.Load() with { Enabled = false }).Save(); Automation?.Revoke(); McpEnabled = false; Status = "Доступ MCP отключён; выданные записи завершатся на безопасной границе."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SyncException) { Error = e.Message; }
    }
    public string BuildMcpConfiguration()
    {
        var host = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ContactMirror.exe");
        var bridge = Path.Combine(AppContext.BaseDirectory, "ContactMirror.Mcp.exe");
        if (!File.Exists(bridge)) bridge = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ContactMirror.Mcp", "bin", "Release", "net10.0", "ContactMirror.Mcp.exe"));
        return JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["contactmirror"] = new { command = bridge, args = new[] { "--host", host } } } }, new JsonSerializerOptions { WriteIndented = true });
    }
}
