using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ContactMirror.McpTests")]
namespace ContactMirror.Infrastructure.Automation;

/// <summary>Terminal publication wins against a cancel request captured while the job was running.</summary>
internal sealed class AgentJobLifecycle
{
    private string state = "running";
    internal string State => Volatile.Read(ref state);
    internal bool RequestCancel() => Interlocked.CompareExchange(ref state,"cancelRequested","running") == "running";
    internal void Complete(string terminal)
    {
        if(terminal is not ("completed" or "failed" or "unknown")) throw new ArgumentException("Expected terminal job state.",nameof(terminal));
        Volatile.Write(ref state,terminal);
    }
}
