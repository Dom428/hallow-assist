// Hallow Assist: the connection to your relay (a WebSocket). One thread receives, one sends in order, so sealed messages
// leave in the same order their counters were taken. Text messages are the relay's own small JSON; binary ones are passed
// to the other end untouched.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HallowAssist
{
    public sealed class RelayLink : ISealedSender, IDisposable
    {
        readonly ClientWebSocket ws = new ClientWebSocket();
        readonly BlockingCollection<Tuple<byte[], bool, Func<byte[]>>> queue = new BlockingCollection<Tuple<byte[], bool, Func<byte[]>>>();
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        public event Action<string> Text;
        public event Action<byte[]> Binary;
        public event Action<string> Closed;   // reason
        int closed; long queuedBytes; readonly ManualResetEvent drained = new ManualResetEvent(false);
        public long Queued { get { return Interlocked.Read(ref queuedBytes); } }
        public long BytesOut, BytesIn;

        public void Connect(Uri uri, int timeoutMs = 10000)
        {
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            using (var t = new CancellationTokenSource(timeoutMs)) ws.ConnectAsync(uri, t.Token).Wait();
            new Thread(ReceiveLoop) { IsBackground = true, Name = "relay receive" }.Start();
            new Thread(SendLoop) { IsBackground = true, Name = "relay send" }.Start();
            new Thread(() => { while (closed == 0) { Thread.Sleep(20000); SendText("{\"t\":\"ping\"}"); } }) { IsBackground = true, Name = "relay ping" }.Start();
        }
        public void SendText(string s) { Enqueue(Encoding.UTF8.GetBytes(s), false, null); }
        public void SendBinary(byte[] b) { Enqueue(b, true, null); }
        // seal at send time, in queue order (the counter order = the wire order)
        // v1.3: counted as gone only AFTER it is sealed, so "nothing queued" really means every counter has been taken
        public void SendSealed(Func<byte[]> make, int sizeHint) { Interlocked.Add(ref queuedBytes, sizeHint); Enqueue(null, true, () => { var b = make(); Interlocked.Add(ref queuedBytes, -sizeHint); return b; }); }
        void Enqueue(byte[] b, bool bin, Func<byte[]> make)
        {
            if (closed != 0) return;
            if (b != null) Interlocked.Add(ref queuedBytes, b.Length);
            try { queue.Add(Tuple.Create(b, bin, make)); } catch { }
        }

        void SendLoop()
        {
            try
            {
                foreach (var item in queue.GetConsumingEnumerable(cts.Token))
                {
                    var data = item.Item1 ?? item.Item3();
                    if (item.Item1 != null) Interlocked.Add(ref queuedBytes, -item.Item1.Length);
                    ws.SendAsync(new ArraySegment<byte>(data), item.Item2 ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, cts.Token).Wait();
                    Interlocked.Add(ref BytesOut, data.Length);
                }
            }
            catch (Exception e) { Close("Lost the connection to the relay (" + Short(e) + ")."); }
            finally { drained.Set(); }
        }
        void ReceiveLoop()
        {
            var buf = new byte[64 * 1024]; var msg = new MemoryStream();
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).Result;
                    if (r.MessageType == WebSocketMessageType.Close) { Close(CloseText(ws.CloseStatusDescription)); return; }
                    msg.Write(buf, 0, r.Count);
                    if (msg.Length > 16 * 1024 * 1024) { Close("A message from the relay was too big."); return; }
                    if (!r.EndOfMessage) continue;
                    var data = msg.ToArray(); msg.SetLength(0);
                    Interlocked.Add(ref BytesIn, data.Length);
                    try
                    {
                        if (r.MessageType == WebSocketMessageType.Text) { var h = Text; if (h != null) h(Encoding.UTF8.GetString(data)); }
                        else { var h = Binary; if (h != null) h(data); }
                    }
                    catch (Exception e) { Close("Session error: " + Short(e)); return; }
                }
                Close("The relay closed the connection.");
            }
            catch (Exception e) { Close("Lost the connection to the relay (" + Short(e) + ")."); }
        }
        static string CloseText(string d) { return string.IsNullOrEmpty(d) ? "The relay closed the connection." : "The relay closed the connection (" + d + ")."; }
        static string Short(Exception e) { while (e is AggregateException && e.InnerException != null) e = e.InnerException; while (e.InnerException != null && e is WebSocketException) e = e.InnerException; return e.Message.TrimEnd('.'); }

        public bool IsOpen { get { return closed == 0; } }
        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref closed, 1) == 1) return;
            // let queued messages (e.g. "bye") go out first, briefly
            try { queue.CompleteAdding(); } catch { }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                drained.WaitOne(1500);
                try { if (ws.State == WebSocketState.Open) ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(1000); } catch { }
                for (int i = 0; i < 40 && ws.State != WebSocketState.Closed && ws.State != WebSocketState.Aborted; i++) Thread.Sleep(50);   // let the relay see everything before the socket goes
                try { cts.Cancel(); } catch { }
                try { ws.Abort(); } catch { }
            });
            var h = Closed; if (h != null) try { h(reason); } catch { }
        }
        public void Dispose() { Close("closed"); }
    }
}
