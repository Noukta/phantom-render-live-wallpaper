using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Wpe.Core;

namespace Wpe.Engine;

/// <summary>
/// Named-pipe server for the control panel. Runs on background threads; every request is handed to the
/// engine's UI thread (via a posted window message) so engine state is only touched from one thread.
/// </summary>
public sealed class ControlServer : IDisposable
{
    public const uint WakeMessage = Native.WM_APP + 20;

    readonly nint _wakeWindow;
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentQueue<(ControlRequest req, TaskCompletionSource<ControlResponse> tcs)> _queue = new();
    readonly List<StreamWriter> _clients = new();

    public ControlServer(nint wakeWindow)
    {
        _wakeWindow = wakeWindow;
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(Control.PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                var p = pipe;
                pipe = null;
                _ = Task.Run(() => ClientLoop(p));
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Log.Write("control accept: " + ex.Message);
                try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    async Task ClientLoop(NamedPipeServerStream pipe)
    {
        var reader = new StreamReader(pipe, Encoding.UTF8);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        lock (_clients) _clients.Add(writer);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line == null) break;
                ControlResponse resp;
                try
                {
                    var req = JsonSerializer.Deserialize(line, WpeWire.Default.ControlRequest);
                    if (req == null) continue;
                    var tcs = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _queue.Enqueue((req, tcs));
                    Native.PostMessage(_wakeWindow, WakeMessage, 0, 0);
                    resp = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), _cts.Token).ConfigureAwait(false);
                }
                catch (JsonException ex) { resp = new ControlResponse { Ok = false, Error = "bad request: " + ex.Message }; }
                catch (TimeoutException) { resp = new ControlResponse { Ok = false, Error = "engine busy" }; }
                string json = JsonSerializer.Serialize(resp, WpeWire.Default.ControlResponse);
                lock (writer) writer.WriteLine(json);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_clients) _clients.Remove(writer);
            pipe.Dispose();
        }
    }

    /// <summary>Runs queued requests. Call from the UI thread when WakeMessage arrives.</summary>
    public void ProcessQueue(Func<ControlRequest, ControlResponse> handler)
    {
        while (_queue.TryDequeue(out var item))
        {
            ControlResponse r;
            try { r = handler(item.req); }
            catch (Exception ex) { r = new ControlResponse { Ok = false, Error = ex.Message }; }
            r.Id = item.req.Id;
            item.tcs.TrySetResult(r);
        }
    }

    /// <summary>Pushes an event to every connected client (fire and forget).</summary>
    public void Broadcast(ControlEvent ev)
    {
        StreamWriter[] clients;
        lock (_clients) { if (_clients.Count == 0) return; clients = _clients.ToArray(); }
        string json = JsonSerializer.Serialize(ev, WpeWire.Default.ControlEvent);
        foreach (var w in clients)
            _ = Task.Run(() => { try { lock (w) w.WriteLine(json); } catch { } });
    }

    public void Dispose() => _cts.Cancel();
}
