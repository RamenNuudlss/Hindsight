using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hindsight
{
    /// <summary>
    /// Minimal obs-websocket v5 client (the server built into OBS 28 and newer).
    /// </summary>
    internal sealed class ObsSocket : IDisposable
    {
        // Event subscription bits from the obs-websocket protocol
        const int SubGeneral = 1 << 0;
        const int SubOutputs = 1 << 6;
        const int SubVolumeMeters = 1 << 16;
        const int SubConfig = 1 << 1;   // profile and scene collection changes
        const int BaseSubs = SubGeneral | SubConfig | SubOutputs;

        ClientWebSocket ws;
        CancellationTokenSource cts;
        readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> pending =
            new ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        int nextId;

        public bool Connected => ws != null && ws.State == WebSocketState.Open && identified;
        volatile bool identified;

        /// <summary>Raised on a background thread: (eventType, eventData).</summary>
        public event Action<string, Dictionary<string, object>> Event;
        public event Action Closed;

        public async Task<bool> ConnectAsync(int port, string password, bool auth, int timeoutMs)
        {
            DisposeSocket();
            cts = new CancellationTokenSource();
            ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            ws.Options.AddSubProtocol("obswebsocket.json");
            var connectCts = new CancellationTokenSource(timeoutMs);
            try
            {
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + port), connectCts.Token).ConfigureAwait(false);
                var hello = await ReceiveOne(connectCts.Token).ConfigureAwait(false);
                if (hello == null || hello.Int("op", -1) != 0) return false;
                var d = hello.Obj("d");
                var identify = new Dictionary<string, object>
                {
                    ["rpcVersion"] = 1,
                    ["eventSubscriptions"] = BaseSubs | SubVolumeMeters,
                };
                var a = d.Obj("authentication");
                if (a != null)
                {
                    string secret = Sha64(password + a.Str("salt"));
                    identify["authentication"] = Sha64(secret + a.Str("challenge"));
                }
                await Send(1, identify, connectCts.Token).ConfigureAwait(false);
                var ident = await ReceiveOne(connectCts.Token).ConfigureAwait(false);
                if (ident == null || ident.Int("op", -1) != 2) return false;
                identified = true;
                var loopToken = cts.Token;
                Task.Run(() => ReceiveLoop(loopToken));
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("obs-websocket connect: " + ex.Message);
                DisposeSocket();
                return false;
            }
        }

        static string Sha64(string s)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }

        async Task Send(int op, Dictionary<string, object> d, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object> { ["op"] = op, ["d"] = d }));
            await sendLock.WaitAsync(token).ConfigureAwait(false);
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
            finally { sendLock.Release(); }
        }

        async Task<Dictionary<string, object>> ReceiveOne(CancellationToken token)
        {
            var buf = new byte[64 * 1024];
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    ms.Write(buf, 0, r.Count);
                    if (r.EndOfMessage) break;
                }
                return Json.Parse(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }

        async Task ReceiveLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var msg = await ReceiveOne(token).ConfigureAwait(false);
                    if (msg == null) break;
                    int op = msg.Int("op", -1);
                    var d = msg.Obj("d");
                    if (op == 7 && d != null)
                    {
                        TaskCompletionSource<Dictionary<string, object>> tcs;
                        if (pending.TryRemove(d.Str("requestId", ""), out tcs)) tcs.TrySetResult(d);
                    }
                    else if (op == 5 && d != null)
                    {
                        try { Event?.Invoke(d.Str("eventType", ""), d.Obj("eventData") ?? new Dictionary<string, object>()); }
                        catch (Exception ex) { Log.Error("event handler", ex); }
                    }
                }
            }
            catch (Exception ex) { if (!token.IsCancellationRequested) Log.Info("obs-websocket closed: " + ex.Message); }
            identified = false;
            foreach (var p in pending) p.Value.TrySetException(new IOException("OBS disconnected"));
            pending.Clear();
            if (!token.IsCancellationRequested) Closed?.Invoke();
        }

        public sealed class Reply
        {
            public bool Ok;
            public int Code;
            public string Comment;
            public Dictionary<string, object> Data = new Dictionary<string, object>();
        }

        public async Task<Reply> Request(string type, Dictionary<string, object> data = null, int timeoutMs = 8000)
        {
            if (!Connected) return new Reply { Ok = false, Comment = "Not connected to OBS" };
            string id = Interlocked.Increment(ref nextId).ToString();
            var tcs = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            var req = new Dictionary<string, object> { ["requestType"] = type, ["requestId"] = id };
            if (data != null) req["requestData"] = data;
            try
            {
                await Send(6, req, cts.Token).ConfigureAwait(false);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (done != tcs.Task) { pending.TryRemove(id, out tcs); return new Reply { Ok = false, Comment = "OBS didn't answer" }; }
                var d = tcs.Task.Result;
                var st = d.Obj("requestStatus");
                return new Reply
                {
                    Ok = st != null && st.Bool("result"),
                    Code = st?.Int("code") ?? 0,
                    Comment = st?.Str("comment"),
                    Data = d.Obj("responseData") ?? new Dictionary<string, object>(),
                };
            }
            catch (Exception ex)
            {
                pending.TryRemove(id, out tcs);
                return new Reply { Ok = false, Comment = ex.Message };
            }
        }

        void DisposeSocket()
        {
            identified = false;
            try { cts?.Cancel(); } catch { }
            try { ws?.Abort(); } catch { }
            try { ws?.Dispose(); } catch { }
            ws = null;
        }

        public void Dispose() { DisposeSocket(); }
    }
}
