using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using ProtoBuf;
using Newtonsoft.Json;

namespace WsTunnelClient
{
    static class ConsoleUtf8
    {
        [DllImport("kernel32.dll")] static extern bool SetConsoleOutputCP(uint id);
        [DllImport("kernel32.dll")] static extern bool SetConsoleCP(uint id);

        public static void Enable()
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    SetConsoleOutputCP(65001); // UTF-8 output
                    SetConsoleCP(65001);       // UTF-8 input
                }
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                Console.InputEncoding = new System.Text.UTF8Encoding(false);
            }
            catch { }
        }
    }

    internal static class Program
    {
        // === Heartbeat / reconnect (4.7.2-safe) ===
        private static long _lastPongMs = 0;                // last seen pong (ms since epoch)
        private const long PONG_TIMEOUT_MS = 120000;        // 120s — больше терпимости при больших загрузках
        private static int _reconnectRequested = 0;         // guard to avoid multiple aborts
        // ==========================================

        // Глобальный ограничитель исходящего JSON-трафика (троттлинг, чтобы не забивать сервер)
        private static long _sendBudgetBytes = 2 * 1024 * 1024; // max "в полёте"
        private const long SEND_BUDGET_MAX = 2 * 1024 * 1024;
        private const int SEND_BUDGET_LOW_DELAY_MS = 2;
        // === DNS resolve settings (client-side) ===
        // Modes: "server" (no client resolve), "client" (always resolve to IP), "auto" (try client, fallback to server)
        private static string _resolveMode = "auto";
        private static int _dnsTimeoutMs = 1500;
        private static bool _preferIPv4 = true;
        // =========================================
        // короткий сон при исчерпании бюджета

        private const string WsUrl = "ws://185.231.245.228:8080/ws";
        private const string RegPath = @"Software\WsTunnelClient";
        private const string RegNameClientId = "ClientId";
        private static readonly string ClientIdFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".client-id");

        private static WsClient _ws;
        private static CancellationTokenSource _cts;
        private static Timer _pingTimer;
        private static readonly object SendLock = new object();
        private static readonly System.Threading.SemaphoreSlim _sendGate = new System.Threading.SemaphoreSlim(1, 1);

        static readonly object _printLock = new object();
        static System.IO.StreamWriter _log;
        // ===== UI/Status logging (non-spam) =====
        private static string _lastStatusKey = null;
        private static bool _helloOk = false; // only announce Connected after server replies hello-ok
        private static void PrintStatus(string key, string message, ConsoleColor color)
        {
            if (key != null && _lastStatusKey == key) return;
            _lastStatusKey = key;
            var prev = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine(message);
            }
            finally { Console.ForegroundColor = prev; }
        }
        private static void ResetStatusKey() { _lastStatusKey = null; }


        private static string _clientId;
        private static string _info;

        // connId -> connection
        private class Conn
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public volatile bool Ending = false;
            public byte[] ReadBuffer = new byte[8 * 1024]; // Меньше чанки — меньше пиков backpressure
        }
        private static readonly ConcurrentDictionary<string, Conn> _conns = new ConcurrentDictionary<string, Conn>();


        static void InitLog()
        {
            try
            {
                var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client.log");
                _log = new System.IO.StreamWriter(
                    new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read),
                    new System.Text.UTF8Encoding(false));
                _log.AutoFlush = TrueOrFalse(true);
            }
            catch { }
        }

        static bool TrueOrFalse(bool v) { return v; } // helper to avoid older compiler complaints about property init
        public static void Main(string[] args)
        {
            ConsoleUtf8.Enable();
            InitLog();
            try { PrintStatus("start", "[!] Start client…", ConsoleColor.DarkGray); Run().Wait(); }
            catch (Exception ex) { Console.WriteLine("Fatal: " + ex); }
        }

        private static async Task Run()
        {
            _clientId = LoadClientId();
            _info = GetWindowsInfo();

            int attempt = 0;
            for (; ; )
            {
                attempt++;
                try
                {
                    _cts = new CancellationTokenSource();
                    _ws = new WsClient();
                    _ws.SetKeepAlive(TimeSpan.FromSeconds(30));
                    try { _ws.SetBuffer(16 * 1024, 16 * 1024); } catch { }
                    PrintStatus("connecting", "[!] Connecting…", ConsoleColor.Yellow);
                    _helloOk = false;
                    await _ws.ConnectAsync(new Uri(WsUrl), _cts.Token);
                    _ws.OnMessage += (bytes, type) =>
                    {
                        try
                        {
                            if (type == WebSocketMessageType.Binary)
                            {
                                var jsonText = System.Text.Encoding.UTF8.GetString(bytes);
                                HandleMessage(jsonText);
                            }
                            else
                            {
                                // Если сервер перейдет на бинарный протокол — обработать тут
                                // HandleBinary(bytes);
                            }
                        }
                        catch (Exception ex) { Print("[msg error] " + ex.Message, ConsoleColor.Red); }
                    };
                    _ws.OnDisconnected += (code, reason) => { PrintStatus("disconnected", $"[-] Disconnected: {code} {reason}", ConsoleColor.Yellow); };
                    // connected TCP/WS, waiting hello-ok…

                    // compute self-hash (SHA-256) at runtime
                    string authHash = null;
                    try
                    {
                        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        byte[] exeBytes = File.ReadAllBytes(exePath);
                        using (var sha = SHA256.Create())
                        {
                            byte[] h = sha.ComputeHash(exeBytes);
                            var sb = new StringBuilder(h.Length * 2);
                            for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                            authHash = sb.ToString();
                        }
                    }
                    catch (Exception exHash)
                    {
                        Console.WriteLine("[auth] hash error: " + exHash.Message);
                        return;
                    }

                    // hello

                    // hello (protobuf)
                    var hello = new Envelope
                    {
                        type = Type.HELLO,
                        authHash = authHash,
                        clientId = string.IsNullOrEmpty(_clientId) ? "" : _clientId,
                        info = string.IsNullOrEmpty(_info) ? "Windows" : _info,
                        t = NowMs()
                    };
                    await SendProtoAsync(hello, _cts.Token);
                    Interlocked.Exchange(ref _lastPongMs, NowMs());

                    // ping timer
                    _pingTimer = new Timer(async _ =>
                    {
                        try
                        {

                            var ping = new Envelope { type = Type.PING, t = NowMs() };
                            await SendProtoAsync(ping, CancellationToken.None);
                            var last = Interlocked.Read(ref _lastPongMs);
                            var now = NowMs();
                            if (last > 0 && (now - last) > PONG_TIMEOUT_MS)
                            {
                                PrintStatus("connect-wait", "[W] Connect server error – waiting connect", ConsoleColor.Red);
                                RequestReconnect("pong-timeout");
                            }
                        }
                        catch { }
                    }, null, 15000, 25000);

                    // receive
                    await ReceiveLoop(_cts.Token);
                }
                catch (WebSocketException wse)
                {
                    var msg = wse.Message ?? string.Empty;
                    if (msg.IndexOf("Aborted", StringComparison.OrdinalIgnoreCase) >= 0 && Interlocked.CompareExchange(ref _reconnectRequested, 0, 0) == 1)
                    {
                        PrintStatus("connect-wait", "[W] Connect server error – waiting connect", ConsoleColor.Red);
                    }
                    else
                    {
                        PrintStatus("connect-wait", "[W] Connect server error – waiting connect", ConsoleColor.Red);
                    }
                }
                catch (Exception ex)
                {
                    PrintStatus("ws-error", "[W] Error: " + ex.Message, ConsoleColor.Red);
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnectRequested, 0);
                    ResetStatusKey();
                    try { _pingTimer?.Dispose(); } catch { }
                    CleanupAllTcp();
                    try
                    {
                        if (_ws != null && (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseSent))
                            _ws.Abort();
                    }
                    catch { }
                }

                // backoff
                int delay = attempt * 1000;
                if (delay > 30000) delay = 30000;
                await Task.Delay(delay);
            }
        }

        private static void Print(string s, ConsoleColor color)
        {
            lock (_printLock)
            {
                var prev = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = color;
                    Console.WriteLine(s);
                }
                finally { Console.ForegroundColor = prev; }
                try { _log?.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s); } catch { }
            }
        }

        private static async Task ReceiveLoop(CancellationToken ct)
        {
            var buffer = new ArraySegment<byte>(new byte[128 * 1024]);
            for (; ; )
            {
                using (var ms = new MemoryStream())
                {
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await _ws.ReceiveAsync(buffer, ct);
                        if (res.MessageType == WebSocketMessageType.Close)
                        {
                            PrintStatus("connect-wait", "[W] Connect server error – waiting connect", ConsoleColor.Red);
                            if (res.CloseStatus == WebSocketCloseStatus.PolicyViolation &&
                                res.CloseStatusDescription != null &&
                                res.CloseStatusDescription.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Environment.Exit(1);
                            }
                            return;
                        }
                        ms.Write(buffer.Array, buffer.Offset, res.Count);
                    }
                    while (!res.EndOfMessage);


                    ms.Position = 0;
                    Envelope env = null;
                    try { env = Serializer.Deserialize<Envelope>(ms); }
                    catch (Exception ex) { PrintStatus("ws-decode", "[W] Error: " + ex.Message, ConsoleColor.Red); continue; }
                    HandleEnvelope(env);
                }
            }
        }

        private static void HandleMessage(string jsonText)
        {
            try
            {
                var msg = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonText);
                if (msg == null) return;
                var type = GetString(msg, "type");

                if (type == "pong")
                {
                    Interlocked.Exchange(ref _lastPongMs, NowMs());
                    return;
                }

                if (type == "hello-ok")
                {
                    var idFromServer = GetString(msg, "clientId");
                    if (!string.IsNullOrEmpty(idFromServer) && idFromServer != _clientId)
                    {
                        _clientId = idFromServer; SaveClientId(_clientId);
                        Console.WriteLine("[ws] clientId set: " + _clientId);
                    }
                    _helloOk = true;
                    PrintStatus("connected", "[!] Connected", ConsoleColor.Green);

                    return;
                }
                if (type == "open")
                {
                    var connId = GetString(msg, "connId");
                    var host = GetString(msg, "host");
                    var portObj = GetNumber(msg, "port");
                    int port = portObj.HasValue ? (int)portObj.Value : 0;
                    if (!string.IsNullOrEmpty(connId) && !string.IsNullOrEmpty(host) && port > 0 && port <= 65535)
                        StartTcp(connId, host, port);
                    return;
                }
                if (type == "data")
                {
                    var connId = GetString(msg, "connId");
                    var dataB64 = GetString(msg, "data");
                    if (!string.IsNullOrEmpty(connId) && !string.IsNullOrEmpty(dataB64))
                    {
                        var conn = GetConn(connId);
                        if (conn != null && conn.Stream != null && !conn.Cts.IsCancellationRequested)
                        {
                            try
                            {
                                byte[] buf = Convert.FromBase64String(dataB64);
                                lock (conn.WriteLock)
                                {
                                    if (!conn.Cts.IsCancellationRequested && conn.Stream.CanWrite)
                                        conn.Stream.Write(buf, 0, buf.Length); // sync write under lock
                                }
                            }
                            catch (ObjectDisposedException)
                            {
                                // stream already closed as part of EndTcp; ignore
                            }
                            catch (Exception exw)
                            {
                                PrintStatus("tcp-write-error", "[W] Error: " + exw.Message, ConsoleColor.Red);
                                EndTcp(connId).Wait();
                            }
                        }
                    }
                    return;
                }
                if (type == "end")
                {
                    var connId = GetString(msg, "connId");
                    if (!string.IsNullOrEmpty(connId)) EndTcp(connId).Wait();
                    return;
                }
            }
            catch (Exception ex) { PrintStatus("ws-msg-error", "[W] Error: " + ex.Message, ConsoleColor.Red); }
        }


        private static void HandleEnvelope(Envelope env)
        {
            try
            {
                if (env == null) return;
                switch (env.type)
                {
                    case Type.PONG:
                        Interlocked.Exchange(ref _lastPongMs, NowMs());
                        return;

                    case Type.HELLO_OK:
                        if (!string.IsNullOrEmpty(env.clientId) && env.clientId != _clientId)
                        {
                            _clientId = env.clientId; SaveClientId(_clientId);
                            Console.WriteLine("[ws] clientId set: " + _clientId);
                        }
                        _helloOk = true;
                        PrintStatus("connected", "[!] Connected", ConsoleColor.Green);
                        return;

                    case Type.OPEN:
                        if (!string.IsNullOrEmpty(env.connId) && !string.IsNullOrEmpty(env.host) && env.port > 0 && env.port <= 65535)
                            StartTcp(env.connId, env.host, (int)env.port);
                        return;

                    case Type.DATA:
                        if (!string.IsNullOrEmpty(env.connId) && env.data != null && env.data.Length > 0)
                        {
                            var conn = GetConn(env.connId);
                            if (conn != null && conn.Stream != null && !conn.Cts.IsCancellationRequested)
                            {
                                try
                                {
                                    lock (conn.WriteLock)
                                    {
                                        if (!conn.Cts.IsCancellationRequested && conn.Stream.CanWrite)
                                            conn.Stream.Write(env.data, 0, env.data.Length);
                                    }
                                }
                                catch (ObjectDisposedException) { }
                                catch (IOException) { }
                                catch (Exception ex) { PrintStatus("tcp-write-error", "[W] Error: " + ex.Message, ConsoleColor.Red); }
                            }
                        }
                        return;

                    case Type.END:
                        if (!string.IsNullOrEmpty(env.connId)) EndTcp(env.connId).Wait();
                        return;
                }
            }
            catch (Exception ex) { PrintStatus("ws-msg-error", "[W] Error: " + ex.Message, ConsoleColor.Red); }
        }

        // === DNS helpers ===
        private static bool LooksLikeIp(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            IPAddress ip;
            return IPAddress.TryParse(host, out ip);
        }

        private static async Task<string> MaybeResolveAsync(string host)
        {
            if (string.IsNullOrEmpty(host) || LooksLikeIp(host)) return host;
            if (string.Equals(_resolveMode, "server", StringComparison.OrdinalIgnoreCase)) return host;

            try
            {
                var dnsTask = Dns.GetHostAddressesAsync(host);
                var completed = await Task.WhenAny(dnsTask, Task.Delay(_dnsTimeoutMs)).ConfigureAwait(false);
                if (completed != dnsTask) return host; // timeout

                var addrs = dnsTask.Result;
                if (addrs == null || addrs.Length == 0) return host;

                IPAddress chosen = null;
                if (_preferIPv4)
                {
                    foreach (var a in addrs) { if (a.AddressFamily == AddressFamily.InterNetwork) { chosen = a; break; } }
                    if (chosen == null) chosen = addrs[0];
                }
                else
                {
                    foreach (var a in addrs) { if (a.AddressFamily == AddressFamily.InterNetworkV6) { chosen = a; break; } }
                    if (chosen == null) chosen = addrs[0];
                }

                if (chosen != null && (string.Equals(_resolveMode, "client", StringComparison.OrdinalIgnoreCase) || string.Equals(_resolveMode, "auto", StringComparison.OrdinalIgnoreCase)))
                    return chosen.ToString();
            }
            catch { /* ignore */ }
            return host;
        }
        // ====================

        // ===== TCP =====
        private static void StartTcp(string connId, string host, int port)
        {
            Task.Run(async () =>
            {
                Conn conn = null;
                try
                {
                    var client = new TcpClient();
                    client.NoDelay = true;
                    var __resolvedHost = await MaybeResolveAsync(host).ConfigureAwait(false);
                    await client.ConnectAsync(__resolvedHost, port);
                    var ns = client.GetStream();
                    conn = new Conn { Client = client, Stream = ns };
                    AddConn(connId, conn);
                    // (quiet) tcp open log removed per request


                    var opened = new Envelope { type = Type.OPENED, connId = connId };
                    await SendProtoAsync(opened, CancellationToken.None);
                    while (!conn.Cts.IsCancellationRequested)
                    {
                        int read = 0;
                        try
                        {
                            read = await ns.ReadAsync(conn.ReadBuffer, 0, conn.ReadBuffer.Length);
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (IOException) { break; }
                        catch (Exception exr)
                        {
                            PrintStatus("tcp-read-error", "[W] Error: " + exr.Message, ConsoleColor.Red);
                            break;
                        }
                        if (read <= 0) break;


                        // Send binary data via protobuf
                        var dataMsg = new Envelope { type = Type.DATA, connId = connId, data = new byte[read] };
                        Buffer.BlockCopy(conn.ReadBuffer, 0, dataMsg.data, 0, read);
                        await SendProtoAsync(dataMsg, CancellationToken.None);
                    }
                }
                catch (Exception ex) { PrintStatus("tcp-open-error", "[W] Error: " + ex.Message, ConsoleColor.Red); }
                finally { await EndTcp(connId); }
            });
        }

        private static async Task EndTcp(string connId)
        {
            Conn c;
            if (_conns.TryGetValue(connId, out c))
            {
                if (c.Ending) return; // already closing
                c.Ending = true;
            }
            else
            {
                return;
            }

            try { c.Cts.Cancel(); } catch { }
            try { if (c.Stream != null) c.Stream.Close(); } catch { }
            try { if (c.Client != null) c.Client.Close(); } catch { }
            _conns.TryRemove(connId, out _);

            var end = new Envelope { type = Type.END, connId = connId };
            await SendProtoAsync(end, CancellationToken.None);
            // (quiet) tcp end log removed per request
        }

        private static void AddConn(string connId, Conn c)
        {
            Conn old;
            if (_conns.TryGetValue(connId, out old))
            {
                try { if (old.Stream != null) old.Stream.Close(); } catch { }
                try { if (old.Client != null) old.Client.Close(); } catch { }
                _conns.TryRemove(connId, out _);
            }
            _conns[connId] = c;
        }

        private static Conn GetConn(string connId)
        {
            Conn c;
            if (_conns.TryGetValue(connId, out c)) return c;
            return null;
        }

        private static void CleanupAllTcp()
        {
            string[] keys = new string[_conns.Keys.Count];
            _conns.Keys.CopyTo(keys, 0);
            for (int i = 0; i < keys.Length; i++)
            {
                try { EndTcp(keys[i]).Wait(); } catch { }
            }
        }

        // ===== Protobuf send =====
        private static async Task SendProtoAsync(Envelope env, CancellationToken ct)
        {
            try
            {
                byte[] data;
                using (var ms = new MemoryStream())
                {
                    Serializer.Serialize(ms, env);
                    data = ms.ToArray();
                }

                int sz = data.Length;
                while (Interlocked.Read(ref _sendBudgetBytes) < sz)
                {
                    if (Interlocked.CompareExchange(ref _reconnectRequested, 0, 0) == 1) return;
                    await Task.Delay(SEND_BUDGET_LOW_DELAY_MS, ct).ConfigureAwait(false);
                }
                Interlocked.Add(ref _sendBudgetBytes, -sz);
                try
                {
                    await _sendGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (_ws == null || _ws.State != WebSocketState.Open || Interlocked.CompareExchange(ref _reconnectRequested, 0, 0) == 1)
                            return;
                        await _ws.SendAsync(new ArraySegment<byte>(data, 0, data.Length), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { return; }
                    finally
                    {
                        try { _sendGate.Release(); } catch { }
                    }
                }
                finally
                {
                    Interlocked.Add(ref _sendBudgetBytes, sz);
                    if (Interlocked.Read(ref _sendBudgetBytes) > SEND_BUDGET_MAX) Interlocked.Exchange(ref _sendBudgetBytes, SEND_BUDGET_MAX);
                }
            }
            catch (Exception ex) { Console.WriteLine("[ws] send error: " + ex.Message); }
        }
        private static void RequestReconnect(string reason)
        {
            try
            {
                if (Interlocked.Exchange(ref _reconnectRequested, 1) == 0)
                {
                    PrintStatus("connect-wait", "[W] Connect server error – waiting connect", ConsoleColor.Red);
                    _helloOk = false;
                    try { _cts?.Cancel(); } catch { }
                    try { _ws?.Abort(); } catch { }
                }
            }
            catch { }
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        private static string LoadClientId()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegPath))
                {
                    var id = key.GetValue(RegNameClientId) as string;
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            catch { }
            try
            {
                if (File.Exists(ClientIdFile))
                {
                    var id = File.ReadAllText(ClientIdFile).Trim();
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            catch { }
            return null;
        }

        private static void SaveClientId(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try { using (var key = Registry.CurrentUser.CreateSubKey(RegPath)) { key.SetValue(RegNameClientId, id); } }
            catch { }
            try { File.WriteAllText(ClientIdFile, id); } catch { }
        }

        private static string GetWindowsInfo()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    object o = key.GetValue("ProductName"); string name = o as string;
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }
            return "Windows";
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object v; if (dict.TryGetValue(key, out v)) { if (v == null) return null; if (v is string) return (string)v; return Convert.ToString(v); }
            return null;
        }

        private static double? GetNumber(Dictionary<string, object> dict, string key)
        {
            object v;
            if (dict.TryGetValue(key, out v))
            {
                try
                {
                    if (v == null) return null;
                    if (v is Int32) return (int)v;
                    if (v is Int64) return (long)v;
                    if (v is Double) return (double)v;
                    if (v is Decimal) return (double)(decimal)v;
                    double d; if (Double.TryParse(Convert.ToString(v), out d)) return d;
                }
                catch { }
            }
            return null;
        }
    }
}
