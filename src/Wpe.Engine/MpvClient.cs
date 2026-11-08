using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Wpe.Core;

namespace Wpe.Engine;

/// <summary>One mpv.exe process rendering into a window, controlled over its JSON IPC pipe.</summary>
public sealed class MpvClient : IDisposable
{
    static int s_counter;
    static nint s_job;

    readonly string _pipeName;
    readonly object _lock = new();
    readonly List<string> _pending = new();
    Process? _process;
    NamedPipeClientStream? _pipe;
    CancellationTokenSource? _cts;

    public MpvClient() { _pipeName = $"wpe_mpv_{Environment.ProcessId}_{Interlocked.Increment(ref s_counter)}"; }

    public bool Alive
    {
        get { try { return _process != null && !_process.HasExited; } catch { return false; } }
    }

    public Process? Process => _process;

    public void Start(string mpvExe, nint wid, string file, Settings s)
    {
        Stop();
        var args = new StringBuilder();
        args.Append("--no-config --wid=").Append((long)wid)
            .Append(" --loop-file=inf --keep-open=yes --no-osc --osd-level=0 --input-cursor=no --no-input-default-bindings")
            .Append(" --panscan=1.0 --hwdec=").Append(s.Hwdec)
            // Frame grabs as JPEG: a 4K PNG is ~25 MB and too slow to write when a game starts.
            .Append(" --screenshot-format=jpg --screenshot-jpeg-quality=90")
            .Append(" --really-quiet --no-terminal --input-ipc-server=\\\\.\\pipe\\").Append(_pipeName);
        if (s.Mute) args.Append(" --no-audio");
        args.Append(" \"").Append(file).Append('"');

        var psi = new ProcessStartInfo(mpvExe, args.ToString()) { UseShellExecute = false, CreateNoWindow = true };
        _process = System.Diagnostics.Process.Start(psi);
        if (_process == null) return;
        try { if (s.MpvPriority == "belowNormal") _process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        AddToJob(_process);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ConnectLoop(token));
    }

    /// <summary>mpv dies with the engine, even if the engine crashes.</summary>
    static unsafe void AddToJob(Process p)
    {
        try
        {
            if (s_job == 0)
            {
                s_job = Native.CreateJobObject(0, 0);
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = default;
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                Native.SetInformationJobObject(s_job, 9, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            }
            Native.AssignProcessToJobObject(s_job, p.Handle);
        }
        catch (Exception ex) { Log.Write("job: " + ex.Message); }
    }

    async Task ConnectLoop(CancellationToken token)
    {
        for (int i = 0; i < 100 && !token.IsCancellationRequested; i++)
        {
            try
            {
                var p = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await p.ConnectAsync(200, token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (token.IsCancellationRequested) { p.Dispose(); return; }
                    _pipe = p;
                    foreach (string json in _pending) WriteLocked(json);
                    _pending.Clear();
                }
                _ = Task.Run(() => DrainLoop(p, token));
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (TimeoutException) { }
            catch (IOException) { await Task.Delay(100, token).ConfigureAwait(false); }
        }
        Log.Write("mpv ipc: could not connect to " + _pipeName);
    }

    // mpv answers every command; read and discard so its pipe never fills up.
    static async Task DrainLoop(NamedPipeClientStream p, CancellationToken token)
    {
        byte[] buf = new byte[4096];
        try { while (!token.IsCancellationRequested && await p.ReadAsync(buf, token).ConfigureAwait(false) > 0) { } }
        catch { }
    }

    /// <summary>Sends a command such as ("set_property", "pause", "true"). Queued until the pipe is connected.</summary>
    public void Command(params string[] args)
    {
        var sb = new StringBuilder("{\"command\":[");
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(',');
            string a = args[i];
            if (a is "true" or "false" || double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                sb.Append(a);
            else
                sb.Append(Quote(a));
        }
        sb.Append("]}");
        string json = sb.ToString();
        lock (_lock)
        {
            if (_pipe == null) { _pending.Add(json); return; }
            WriteLocked(json);
        }
    }

    void WriteLocked(string json)
    {
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(json + "\n");
            _pipe!.Write(b, 0, b.Length);
            _pipe.Flush();
        }
        catch (Exception ex) { Log.Write("mpv ipc write: " + ex.Message); }
    }

    static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public void Stop()
    {
        _cts?.Cancel();
        lock (_lock)
        {
            _pending.Clear();
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }
        if (_process != null)
        {
            try { if (!_process.HasExited) { _process.Kill(); _process.WaitForExit(2000); } } catch { }
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose() => Stop();
}
