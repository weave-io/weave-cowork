using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CoWork.Runner.Acp;

/// <summary>A harness running as a child process in its own process group, with an <see cref="AcpClient"/> on its stdio.
/// We only ever signal our own child's group (by pid), never by name.</summary>
public sealed class AcpAgentProcess : IAsyncDisposable
{
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);

    private readonly Process _process;
    private readonly bool _ownGroup;
    private readonly StringBuilder _stderr = new();
    public AcpClient Client { get; }
    public bool IsRunning => !_process.HasExited;
    public int ProcessId => _process.Id;

    public string Stderr { get { lock (_stderr) return _stderr.ToString().Trim(); } }

    private AcpAgentProcess(Process p, bool ownGroup, AcpClient client) { _process = p; _ownGroup = ownGroup; Client = client; }

    public static AcpAgentProcess Start(string[] command, string workingDirectory)
    {
        var args = command.ToList();
        // `setsid` makes the harness a process group leader (pgid == pid) on Linux, so we can stop it and its children together.
        var useSetsid = OperatingSystem.IsLinux() && File.Exists("/usr/bin/setsid");
        if (useSetsid) args.Insert(0, "/usr/bin/setsid");
        var psi = new ProcessStartInfo
        {
            FileName = args[0], WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start " + command[0]);
        var client = new AcpClient(p.StandardOutput, p.StandardInput);
        var agent = new AcpAgentProcess(p, useSetsid, client);
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (agent._stderr) { agent._stderr.AppendLine(e.Data); if (agent._stderr.Length > 8192) agent._stderr.Remove(0, agent._stderr.Length - 8192); }
        };
        p.BeginErrorReadLine();
        client.Start();
        return agent;
    }

    public async ValueTask DisposeAsync()
    {
        try { _process.StandardInput.Close(); } catch { }
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { Kill(); }
        await Client.DisposeAsync();
    }

    public void Kill()
    {
        try
        {
            if (_process.HasExited) return;
            if (_ownGroup) kill(-_process.Id, 15); else _process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
