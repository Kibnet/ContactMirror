using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace ContactMirror.Desktop.Contacts;

internal sealed record CardRowIdentity(JsonNode Node, JsonArray Parent, int Index, string Path);
internal static class CardRows
{
    private static readonly ConditionalWeakTable<Control, CardRowIdentity> owners = new();
    private static readonly ConditionalWeakTable<JsonNode, Identity> ids = new();
    private sealed class Identity { public string Id { get; } = Guid.NewGuid().ToString("N"); }
    public static void Mark(Control root, JsonNode? node, string path)
    {
        if (node?.Parent is JsonArray array) owners.Add(root, new(node, array, array.IndexOf(node), path));
    }
    private static CardRowIdentity? Owner(Control control) => control.GetVisualAncestors().OfType<Control>().Prepend(control).Select(c => owners.TryGetValue(c, out var row) ? row : null).FirstOrDefault(row => row is not null);
    public static string Key(Control control, string id)
    {
        var row = Owner(control);
        return row is null ? id : ids.GetValue(row.Node, _ => new()).Id + ":" + id.Replace(row.Path, "@row", StringComparison.Ordinal);
    }
    public static Func<string> Remap(Control control, string id)
    {
        var row = Owner(control); if (row is null) return () => id;
        var lastDot = row.Path.LastIndexOf('.'); var prefix = row.Path[..lastDot];
        return () =>
        {
            var index = Enumerable.Range(0, row.Parent.Count).FirstOrDefault(i => ReferenceEquals(row.Parent[i], row.Node), -1);
            if (index < 0) index = Math.Min(row.Index, row.Parent.Count - 1);
            return index < 0 ? "AddField:" + prefix.Split('.').Last() : id.Replace(row.Path, prefix + "." + index, StringComparison.Ordinal);
        };
    }
}

public sealed partial class ContactsView
{
    private (string? Root, Guid? Id, bool Editing)? viewportOwner;
    private bool navigationRebuild;
    private readonly Dictionary<string, bool> expandedSections = new();
    private long viewportVersion;
    private sealed record ViewportSnapshot(Vector Offset, Func<string>? Anchor, double AnchorY, Func<string>? Focus, int SelectionStart, int SelectionEnd, IReadOnlyDictionary<string, Vector> Lists);
    private ViewportSnapshot? pendingViewport;
    private Action RestoreViewport()
    {
        var owner = (vm.Root, vm.Original?.Id, vm.IsEditing);
        var explicitNavigation = navigationRebuild;
        var same = viewportOwner == owner;
        viewportOwner = owner;
        if (!same) { expandedSections.Clear(); pendingViewport = null; }
        var scroll = same ? fieldScroll : null;
        var offset = scroll?.Offset ?? default;
        var controls = scroll?.GetVisualDescendants().OfType<Control>().ToArray() ?? [];
        foreach (var expander in pendingViewport is null ? controls.OfType<Expander>() : [])
            if (AutomationProperties.GetAutomationId(expander) is { Length: > 0 } id) expandedSections[CardRows.Key(expander, id)] = expander.IsExpanded;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var focusId = focused is not null && controls.Contains(focused) ? AutomationProperties.GetAutomationId(focused) : null;
        var remapFocus = focusId is not null ? CardRows.Remap(focused!, focusId) : null;
        var selection = focused is TextBox text ? (text.SelectionStart, text.SelectionEnd) : (0, 0);
        var anchor = controls.Where(c => c.IsEffectivelyVisible && AutomationProperties.GetAutomationId(c) is { Length: > 0 })
            .Select(c => (Id: AutomationProperties.GetAutomationId(c), Remap: CardRows.Remap(c, AutomationProperties.GetAutomationId(c)!), Y: (c.TranslatePoint(default, (Control)scroll!.Content!)?.Y ?? -1) - offset.Y))
            .Where(c => c.Y >= 0).OrderBy(c => c.Y).FirstOrDefault();
        var listOffsets = controls.OfType<ListBox>().Where(list => AutomationProperties.GetAutomationId(list) is not null)
            .ToDictionary(list => AutomationProperties.GetAutomationId(list)!, list => list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset ?? default);
        var snapshot = pendingViewport ??= new(offset, anchor.Remap, anchor.Y, remapFocus, selection.Item1, selection.Item2, listOffsets);
        var version = ++viewportVersion;
        return () => Dispatcher.UIThread.Post(() =>
        {
            if (version != viewportVersion || viewportOwner != owner || fieldScroll is not { } current) return;
            // Opening a lazy expander realizes additional controls; restore outer sections first.
            for (var pass = 0; pass < 3; pass++)
                foreach (var expander in current.GetVisualDescendants().OfType<Expander>().ToArray())
                    if (!(explicitNavigation && expander.IsExpanded) && AutomationProperties.GetAutomationId(expander) is { } id && expandedSections.TryGetValue(CardRows.Key(expander, id), out var value)) expander.IsExpanded = value;
            if (explicitNavigation) { pendingViewport = null; return; }
            foreach (var (id, position) in snapshot.Lists)
                if (FindById(id) is ListBox list && list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } inner) inner.Offset = position;
            void Realize(string? id)
            {
                if (id is null || !id.Contains(':')) return;
                var path = id[(id.IndexOf(':') + 1)..].Split('.');
                if (path.Length < 3 || path[0] != "data" || !int.TryParse(path[2], out var index)) return;
                if (FindById("Rows:" + path[1]) is ListBox list && list.ItemsSource is System.Collections.IList items && index >= 0 && index < items.Count) list.ScrollIntoView(items[index]!);
            }
            Realize(snapshot.Focus?.Invoke()); Realize(snapshot.Anchor?.Invoke());
            Dispatcher.UIThread.Post(() =>
            {
                if (version != viewportVersion || viewportOwner != owner || !ReferenceEquals(fieldScroll, current)) return;
                current.Offset = snapshot.Offset;
                foreach (var expander in current.GetVisualDescendants().OfType<Expander>().ToArray())
                    if (AutomationProperties.GetAutomationId(expander) is { } expanderId && expandedSections.TryGetValue(CardRows.Key(expander, expanderId), out var expanded)) expander.IsExpanded = expanded;
                if (snapshot.Anchor?.Invoke() is { } anchorId && FindById(anchorId) is { } existing && existing.TranslatePoint(default, (Control)current.Content!) is { } point)
                    current.Offset = new Vector(snapshot.Offset.X, Math.Max(0, point.Y - snapshot.AnchorY));
                if (snapshot.Focus?.Invoke() is { } id && FindById(id) is { IsEffectivelyVisible: true } target)
                {
                    target.Focus();
                    if (target is TextBox input) { input.SelectionStart = Math.Min(snapshot.SelectionStart, input.Text?.Length ?? 0); input.SelectionEnd = Math.Min(snapshot.SelectionEnd, input.Text?.Length ?? 0); }
                }
                pendingViewport = null;
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }
}
