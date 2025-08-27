using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace WsTunnelClient
{
    public sealed class WsClient : IDisposable
    {
        private ClientWebSocket _ws;
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _life = new CancellationTokenSource();
        private Task _rxTask;
        private TaskCompletionSource<object> _disconnectTcs;
        private TimeSpan _keepAlive = TimeSpan.FromSeconds(20);
        private int _recvBuf = 16 * 1024, _sendBuf = 16 * 1024;

        // Metrics
        private long _bytesSent;
        private long _bytesRecv;
        private long _msgSent;
        private long _msgRecv;

        public event Action OnConnected;
        public event Action<WebSocketCloseStatus?, string> OnDisconnected;
        public event Action<Exception> OnError;
        public event Action<byte[], WebSocketMessageType> OnMessage;

        public WebSocketState State
        {
            get { return _ws != null ? _ws.State : WebSocketState.None; }
        }

        public void SetKeepAlive(TimeSpan keepAlive)
        {
            _keepAlive = keepAlive;
        }

        public void SetBuffer(int receiveBufferSize, int sendBufferSize)
        {
            _recvBuf = receiveBufferSize;
            _sendBuf = sendBufferSize;
        }

        public async Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            DisposeInternal(); // ensure not connected
            _disconnectTcs = new TaskCompletionSource<object>();
            _ws = new ClientWebSocket();
            try { _ws.Options.KeepAliveInterval = _keepAlive; } catch { }
            try { _ws.Options.SetBuffer(_recvBuf, _sendBuf); } catch { }
            await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            OnConnected?.Invoke();

            // Start background receive loop
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, ct);
            _rxTask = Task.Run(() => ReceiveLoop(linked.Token), linked.Token);
        }

        public async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (_ws == null) throw new ObjectDisposedException(nameof(WsClient));
            return await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
        }

        public async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            if (_ws == null) throw new ObjectDisposedException(nameof(WsClient));
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_ws.State != WebSocketState.Open) return;
                await _ws.SendAsync(buffer, type, endOfMessage, ct).ConfigureAwait(false);
                Interlocked.Add(ref _bytesSent, buffer.Count);
                Interlocked.Increment(ref _msgSent);
            }
            finally
            {
                try { _sendGate.Release(); } catch { }
            }
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[_recvBuf > 0 ? _recvBuf : 16 * 1024];
            try
            {
                while (!ct.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
                {
                    var ms = new MemoryStream();
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                        if (res.MessageType == WebSocketMessageType.Close)
                        {
                            SignalDisconnected(_ws.CloseStatus, _ws.CloseStatusDescription);
                            return;
                        }
                        ms.Write(buf, 0, res.Count);
                    } while (!res.EndOfMessage);

                    var data = ms.ToArray();
                    Interlocked.Add(ref _bytesRecv, data.Length);
                    Interlocked.Increment(ref _msgRecv);
                    var handler = OnMessage;
                    if (handler != null) handler(data, res.MessageType);
                }
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex)
            {
                var eh = OnError;
                if (eh != null) eh(ex);
                SignalDisconnected(null, ex.Message);
            }
        }

        private void SignalDisconnected(WebSocketCloseStatus? code, string reason)
        {
            try { _disconnectTcs.TrySetResult(null); } catch { }
            var h = OnDisconnected;
            if (h != null) h(code, reason);
        }

        public Task WaitForDisconnectAsync(CancellationToken ct)
        {
            var t = _disconnectTcs != null ? _disconnectTcs.Task : Task.FromResult<object>(null);
            if (ct.CanBeCanceled)
            {
                var tcs = new TaskCompletionSource<object>();
                var reg = ct.Register(() => tcs.TrySetCanceled());
                t.ContinueWith(_ => { reg.Dispose(); tcs.TrySetResult(null); }, TaskScheduler.Default);
                return tcs.Task;
            }
            return t;
        }

        public void Abort()
        {
            try { _ws?.Abort(); } catch { }
        }

        public async Task CloseOutputAsync(WebSocketCloseStatus status, string reason, CancellationToken ct)
        {
            if (_ws == null) return;
            try { await _ws.CloseOutputAsync(status, reason, ct).ConfigureAwait(false); } catch { }
        }

        public WsMetrics GetMetricsSnapshot()
        {
            return new WsMetrics
            {
                BytesSent = Interlocked.Read(ref _bytesSent),
                BytesReceived = Interlocked.Read(ref _bytesRecv),
                MessagesSent = Interlocked.Read(ref _msgSent),
                MessagesReceived = Interlocked.Read(ref _msgRecv)
            };
        }

        private void DisposeInternal()
        {
            try { _life.Cancel(); } catch { }
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }

        public void Dispose()
        {
            DisposeInternal();
            try { _sendGate.Dispose(); } catch { }
            try { _life.Dispose(); } catch { }
        }
    }

    public sealed class WsMetrics
    {
        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
        public long MessagesSent { get; set; }
        public long MessagesReceived { get; set; }
    }
}
