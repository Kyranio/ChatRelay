using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace ChatRelay.Host;

public sealed class HostProcess : IDisposable
{
    readonly Process _proc;
    public Stream Stdin => _proc.StandardInput.BaseStream;
    public Stream Stdout => _proc.StandardOutput.BaseStream;

    public static HostProcess Start()
    {
        var exe = ResolveExePath()
            ?? throw new FileNotFoundException("ChatRelay.Host.exe not found next to the extension DLL.");

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ChatRelay.Host.exe.");
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Debug.WriteLine("[host] " + e.Data); };
        proc.BeginErrorReadLine();
        return new HostProcess(proc);
    }

    HostProcess(Process proc) => _proc = proc;

    static string? ResolveExePath()
    {
        var dir = Path.GetDirectoryName(typeof(HostProcess).Assembly.Location);
        if (string.IsNullOrEmpty(dir)) return null;
        var candidate = Path.Combine(dir!, "ChatRelay.Host.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) _proc.Kill(); } catch { }
        _proc.Dispose();
    }
}
