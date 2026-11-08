using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Wpe.Core;

namespace Wpe.UI;

/// <summary>Talks to the engine over \\.\pipe\wpe_control and reconnects automatically.</summary>
public sealed class EngineClient : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<ControlResponse>> _pending = new();
    readonly SynchronizationContext _ui;
    StreamWriter? _writer;
    int _nextId;

    public bool Connected { get; private set; }
    public event Action<bool>? ConnectionChanged;
    public event Action<ControlEvent>? EventReceived;

    public EngineClient()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _ = Task.Run(RunAsync);
    }

    async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Control.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1500, _cts.Token);
                var reader = new StreamReader(pipe, Encoding.UTF8);
                _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                SetConnected(true);
                while (!_cts.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(_cts.Token);
                    if (line == null) break;
                    if (line.StartsWith("{\"event\"", StringComparison.Ordinal))
                    {
                        var ev = JsonSerializer.Deserialize(line, WpeWire.Default.ControlEvent);
                        if (ev != null) _ui.Post(_ => EventReceived?.Invoke(ev), null);
                    }
                    else
                    {
                        var resp = JsonSerializer.Deserialize(line, WpeWire.Default.ControlResponse);
                        if (resp != null && _pending.TryRemove(resp.Id, out var tcs)) tcs.TrySetResult(resp);
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch (Exception) { }
            _writer = null;
            SetConnected(false);
            foreach (var p in _pending) p.Value.TrySetCanceled();
            _pending.Clear();
            try { await Task.Delay(1500, _cts.Token); } catch { return; }
        }
    }

    void SetConnected(bool c)
    {
        if (Connected == c) return;
        Connected = c;
        _ui.Post(_ => ConnectionChanged?.Invoke(c), null);
    }

    /// <summary>Sends a request; returns null if the engine is not reachable.</summary>
    public async Task<ControlResponse?> SendAsync(string cmd, string? monitor = null, string? path = null, bool? paused = null)
    {
        var w = _writer;
        if (w == null) return null;
        var req = new ControlRequest { Id = Interlocked.Increment(ref _nextId), Cmd = cmd, Monitor = monitor, Path = path, Paused = paused };
        var tcs = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[req.Id] = tcs;
        try
        {
            string json = JsonSerializer.Serialize(req, WpeWire.Default.ControlRequest);
            lock (w) w.WriteLine(json);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { _pending.TryRemove(req.Id, out _); return null; }
    }

    public void Dispose() => _cts.Cancel();
}
