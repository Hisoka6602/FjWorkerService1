using System.Text;
using Newtonsoft.Json;
using System.Text.Json;
using System.Threading;
using TouchSocket.Core;
using TouchSocket.Sockets;
using FjWorkerService1.Enums;
using FjWorkerService1.Models.Conf;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using FjWorkerService1.Models.Sorting;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace FjWorkerService1.Servers.Sorter {

    public class DefaultSorter : ISorter, IDisposable {
        private readonly ILogger<DefaultSorter> _logger;
        private TcpConnectConfig Config { get; init; }

        private TcpService? _server;
        private TcpClient? _client;
        private int _connectStarted;

        // TouchSocket 4.0.4: 不同模型下连接对象类型可能不同，这里用 object 存，发送/状态用 dynamic 访问。
        private readonly ConcurrentDictionary<string, object> _clients = new();

        private readonly JsonSerializerOptions _jsonOptions = new() {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public DefaultSorter(TcpConnectConfig config, ILogger<DefaultSorter> logger) {
            _logger = logger;
            Config = config ?? new TcpConnectConfig {
                Mode = ConnectType.Client,
                Ip = "127.0.0.1",
                Port = 9000
            };
        }

        public event EventHandler? Disconnected;

        public event EventHandler<SortingCompletedMessage>? SortingCompleted;

        public event EventHandler<ParcelDetectedMessage>? ParcelDetected;

        public void Dispose() {
            try {
                var client = Interlocked.Exchange(ref _client, null);
                if (client != null) {
                    client.Received -= OnClientReceived;
                    client.SafeDispose();
                }

                var server = Interlocked.Exchange(ref _server, null);
                if (server != null) {
                    server.Connected -= OnServerClientConnected;
                    server.Received -= OnServerReceived;

                    server.SafeDispose();
                }

                _clients.Clear();
            }
            catch {
                // ignore
            }
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _connectStarted, 1, 0) != 0) {
                return;
            }

            try {
                switch (Config.Mode) {
                    case ConnectType.Server:
                        await StartServerAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case ConnectType.Client:
                        // 客户端模式：无限重连 + 指数退避（最大 2s）
                        await RunClientReconnectLoopAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        throw new NotSupportedException($"未知连接模式: {Config.Mode}");
                }
            }
            finally {
                Interlocked.Exchange(ref _connectStarted, 0);
            }
        }

        public async Task SendEventAsync(ChuteAssignmentEventArgs chuteAssignment, CancellationToken cancellationToken = default) {
            var payload = JsonSerializer.Serialize(chuteAssignment, _jsonOptions) + "\n";

            _logger.LogInformation("[Sorting][SEND] {Mode} -> {Ip}:{Port} {Payload}", Config.Mode, Config.Ip, Config.Port, Truncate(payload));

            if (Config.Mode == ConnectType.Client) {
                var client = Volatile.Read(ref _client);
                if (client is null) {
                    _logger.LogError("未连接到排序系统（客户端模式）。");
                    return;
                }

                await SendAnyAsync(client, payload).ConfigureAwait(false);
            }
            else {
                var server = Volatile.Read(ref _server);
                if (server is null) {
                    _logger.LogError("排序服务未启动（服务端模式）。");
                    return;
                }

                foreach (var kv in _clients) {
                    await SendAnyAsync(kv.Value, payload).ConfigureAwait(false);
                }
            }
        }

        private async Task StartServerAsync(CancellationToken cancellationToken) {
            if (Volatile.Read(ref _server) != null) {
                return;
            }

            var service = new TcpService();

            var config = new TouchSocketConfig()
                .SetListenIPHosts([new IPHost($"{Config.Ip}:{Config.Port}")])
                .ConfigureContainer(a => { })
                .ConfigurePlugins(_ => { });

            service.Connected += OnServerClientConnected;
            service.Received += OnServerReceived;

            await service.SetupAsync(config).ConfigureAwait(false);
            await service.StartAsync().ConfigureAwait(false);

            if (Interlocked.CompareExchange(ref _server, service, null) is null) {
                _logger.LogInformation("[Sorting] Server started at {Ip}:{Port}", Config.Ip, Config.Port);
                return;
            }

            service.Connected -= OnServerClientConnected;
            service.Received -= OnServerReceived;
            service.SafeDispose();
        }

        private async Task RunClientReconnectLoopAsync(CancellationToken cancellationToken) {
            var backoff = TimeSpan.FromMilliseconds(100);
            var maxBackoff = TimeSpan.FromSeconds(2);

            while (!cancellationToken.IsCancellationRequested) {
                try {
                    // 清理旧连接
                    var staleClient = Interlocked.Exchange(ref _client, null);
                    if (staleClient != null) {
                        try { staleClient.Received -= OnClientReceived; } catch { }
                        try { await staleClient.CloseAsync().ConfigureAwait(false); } catch { }
                        try { staleClient.Dispose(); } catch { }
                    }

                    await StartClientOnceAsync(cancellationToken).ConfigureAwait(false);

                    // 连接成功后重置退避
                    backoff = TimeSpan.FromMilliseconds(100);

                    // 等待断开/取消
                    while (!cancellationToken.IsCancellationRequested && _client != null && _client.Online) {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    }

                    // 断开
                    if (!cancellationToken.IsCancellationRequested) {
                        _logger.LogWarning("[Sorting] Client disconnected from {Ip}:{Port}", Config.Ip, Config.Port);
                        Disconnected?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (OperationCanceledException) {
                    break;
                }
                catch (Exception ex) {
                    _logger.LogWarning(ex, "[Sorting] Client connect failed, will retry: {Ip}:{Port}", Config.Ip, Config.Port);
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }

                if (cancellationToken.IsCancellationRequested) {
                    break;
                }

                var delay = backoff <= maxBackoff ? backoff : maxBackoff;
                try {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                    break;
                }

                var next = TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * 2);
                backoff = next <= maxBackoff ? next : maxBackoff;
            }
        }

        // 单次连接（失败由外层重试）
        private async Task StartClientOnceAsync(CancellationToken cancellationToken) {
            if (Volatile.Read(ref _client) != null) {
                return;
            }

            var client = new TcpClient();

            var config = new TouchSocketConfig()
                .SetRemoteIPHost(new IPHost($"{Config.Ip}:{Config.Port}"))
                .ConfigureContainer(a => { })
                .ConfigurePlugins(_ => { });

            client.Received += OnClientReceived;

            await client.SetupAsync(config).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            if (Interlocked.CompareExchange(ref _client, client, null) is null) {
                _logger.LogInformation("[Sorting] Client connected to {Ip}:{Port}", Config.Ip, Config.Port);
                return;
            }

            client.Received -= OnClientReceived;
            try { await client.CloseAsync().ConfigureAwait(false); } catch { }
            try { client.Dispose(); } catch { }
        }

        // TouchSocket 4.0.4 的事件签名在不同子包/类型上可能不同，这里用 object + dynamic 适配
        private Task OnServerClientConnected(object client, ConnectedEventArgs e) {
            var id = TryGetClientId(client) ?? Guid.NewGuid().ToString("N");
            _clients[id] = client;
            _logger.LogInformation("[Sorting][SERVER] Client connected: {ClientId}, total={Count}", id, _clients.Count);
            return Task.CompletedTask;
        }

        private Task OnServerReceived(object client, ReceivedDataEventArgs e) {
            var msg = TryGetMessage(e);
            if (!string.IsNullOrWhiteSpace(msg)) {
                _logger.LogInformation("[Sorting][RECV][SERVER] {Payload}", Truncate(msg));
                HandleEnvelope(msg);
            }
            return Task.CompletedTask;
        }

        private Task OnClientReceived(object client, ReceivedDataEventArgs e) {
            var msg = TryGetMessage(e);
            if (!string.IsNullOrWhiteSpace(msg)) {
                _logger.LogInformation("[Sorting][RECV][CLIENT] {Payload}", Truncate(msg));
                HandleEnvelope(msg);
            }
            return Task.CompletedTask;
        }

        private static string? TryGetClientId(object client) {
            try {
                dynamic d = client;
                return (string?)d.Id;
            }
            catch {
                return null;
            }
        }

        private static string? TryGetMessage(ReceivedDataEventArgs e) {
            // 1) 如果启用了数据处理适配器（如 TerminatorPackageAdapter），通常优先从 RequestInfo 获取
            if (e.RequestInfo is not null) {
                var text = e.RequestInfo.ToString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            // 2) TouchSocket 4.x 推荐从 Memory 获取（替代 ByteBlock）
            var memory = e.Memory;
            if (memory.IsEmpty) {
                return null;
            }

            // 低分配：直接从 Span 解码
            return Encoding.UTF8.GetString(memory.Span);
        }

        private static async Task SendAnyAsync(object target, string payload) {
            try {
                dynamic d = target;

                // 优先 SendAsync(string)
                try { await d.SendAsync(payload).ConfigureAwait(false); return; } catch { }

                // 其次 Send(string)
                try { d.Send(payload); return; } catch { }

                // 再尝试 byte[]
                var bytes = Encoding.UTF8.GetBytes(payload);
                try { await d.SendAsync(bytes).ConfigureAwait(false); return; } catch { }
                try { d.Send(bytes); return; } catch { }
            }
            catch {
                // ignore
            }
        }

        private void HandleEnvelope(string json) {
            EnvelopeHead? head;
            try {
                head = JsonSerializer.Deserialize<EnvelopeHead>(json, _jsonOptions);
            }
            catch {
                return;
            }

            if (head is null || string.IsNullOrWhiteSpace(head.Type))
                return;

            switch (head.Type) {
                case "ParcelDetected": {
                        var env = JsonConvert.DeserializeObject<ParcelDetectedMessage>(json);

                        if (env != null) {
                            ParcelDetected?.Invoke(this, env);
                        }
                        break;
                    }
                case "SortingCompleted": {
                        var env = JsonConvert.DeserializeObject<SortingCompletedMessage>(json);

                        if (env != null) {
                            SortingCompleted?.Invoke(this, env);
                        }
                        break;
                    }
            }
        }

        private static string Truncate(string s, int max = 500) {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s[..max] + "...";
        }

        private sealed record EnvelopeHead {
            public string? Type { get; init; }
        }

        private sealed record Envelope<T> {
            public required string Type { get; init; }
            public required T Data { get; init; }
        }
    }
}
