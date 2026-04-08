using System.Text;
using Newtonsoft.Json;
using System.Text.Json;
using System.Threading;
using TouchSocket.Core;
using TouchSocket.Sockets;
using FjWorkerService1.Enums;
using FjWorkerService1.Helpers;
using FjWorkerService1.Models.Conf;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using FjWorkerService1.Models.Sorting;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace FjWorkerService1.Servers.Sorter {

    public class DefaultSorter : ISorter, IDisposable {
        private readonly ILogger<DefaultSorter> _logger;
        private TcpConnectConfig Config { get; init; }
        private readonly BoundedEventDispatcher _eventDispatcher;

        private TcpService? _server;
        private TcpClient? _client;
        private int _connectStarted;
        private int _isDisposed;

        /// <summary>
        /// 服务端模式下的客户端连接集合
        /// </summary>
        private readonly ConcurrentDictionary<string, object> _clients = new();

        private readonly JsonSerializerOptions _jsonOptions = new() {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public DefaultSorter(TcpConnectConfig config, ILogger<DefaultSorter> logger) {
            _logger = logger;

            /// <summary>
            /// 改为单 worker，确保上游消息按进入队列的顺序派发
            /// 避免 ParcelDetected / SortingCompleted 多 worker 并发导致业务层顺序抖动
            /// </summary>
            _eventDispatcher = new BoundedEventDispatcher(_logger, "Sorter", capacity: 4096, workers: 1);

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
            if (Interlocked.Exchange(ref _isDisposed, 1) == 1) {
                return;
            }

            try { _eventDispatcher.Dispose(); } catch { }

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

        public async Task SendEventAsync(
            ChuteAssignmentEventArgs chuteAssignment,
            CancellationToken cancellationToken = default) {
            var payload = JsonSerializer.Serialize(chuteAssignment, _jsonOptions) + "\n";

            _logger.LogInformation(
                "[Sorting][SEND] {Mode} -> {Ip}:{Port} {Payload}",
                Config.Mode,
                Config.Ip,
                Config.Port,
                Truncate(payload));

            if (Config.Mode == ConnectType.Client) {
                var client = Volatile.Read(ref _client);
                if (client is null) {
                    _logger.LogError("未连接到排序系统（客户端模式）。");
                    return;
                }

                await SendAnyAsync(client, payload).ConfigureAwait(false);
                return;
            }

            var server = Volatile.Read(ref _server);
            if (server is null) {
                _logger.LogError("排序服务未启动（服务端模式）。");
                return;
            }

            foreach (var item in _clients) {
                await SendAnyAsync(item.Value, payload).ConfigureAwait(false);
            }
        }

        private async Task StartServerAsync(CancellationToken cancellationToken) {
            if (Volatile.Read(ref _server) != null) {
                return;
            }

            var service = new TcpService();

            var config = new TouchSocketConfig()
                .SetListenIPHosts([new IPHost($"{Config.Ip}:{Config.Port}")])
                .ConfigureContainer(_ => { })
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
                    var staleClient = Interlocked.Exchange(ref _client, null);
                    if (staleClient != null) {
                        try { staleClient.Received -= OnClientReceived; } catch { }
                        try { await staleClient.CloseAsync().ConfigureAwait(false); } catch { }
                        try { staleClient.Dispose(); } catch { }
                    }

                    await StartClientOnceAsync(cancellationToken).ConfigureAwait(false);
                    backoff = TimeSpan.FromMilliseconds(100);

                    while (!cancellationToken.IsCancellationRequested && _client != null && _client.Online) {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    }

                    if (!cancellationToken.IsCancellationRequested) {
                        _logger.LogWarning("[Sorting] Client disconnected from {Ip}:{Port}", Config.Ip, Config.Port);
                        PublishDisconnected();
                    }
                }
                catch (OperationCanceledException) {
                    break;
                }
                catch (Exception ex) {
                    _logger.LogWarning(ex, "[Sorting] Client connect failed, will retry: {Ip}:{Port}", Config.Ip, Config.Port);
                    PublishDisconnected();
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

        private async Task StartClientOnceAsync(CancellationToken cancellationToken) {
            if (Volatile.Read(ref _client) != null) {
                return;
            }

            var client = new TcpClient();

            var config = new TouchSocketConfig()
                .SetRemoteIPHost(new IPHost($"{Config.Ip}:{Config.Port}"))
                .ConfigureContainer(_ => { })
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

        private Task OnServerClientConnected(object client, ConnectedEventArgs e) {
            var clientId = TryGetClientId(client) ?? Guid.NewGuid().ToString("N");
            _clients[clientId] = client;
            _logger.LogInformation("[Sorting][SERVER] Client connected: {ClientId}, total={Count}", clientId, _clients.Count);
            return Task.CompletedTask;
        }

        private Task OnServerReceived(object client, ReceivedDataEventArgs e) {
            var message = TryGetMessage(e);
            if (!string.IsNullOrWhiteSpace(message)) {
                _logger.LogInformation("[Sorting][RECV][SERVER] {Payload}", Truncate(message));
                HandleEnvelope(message);
            }

            return Task.CompletedTask;
        }

        private Task OnClientReceived(object client, ReceivedDataEventArgs e) {
            var message = TryGetMessage(e);
            if (!string.IsNullOrWhiteSpace(message)) {
                _logger.LogInformation("[Sorting][RECV][CLIENT] {Payload}", Truncate(message));
                HandleEnvelope(message);
            }

            return Task.CompletedTask;
        }

        private static string? TryGetClientId(object client) {
            try {
                dynamic dynamicClient = client;
                return (string?)dynamicClient.Id;
            }
            catch {
                return null;
            }
        }

        private static string? TryGetMessage(ReceivedDataEventArgs e) {
            if (e.RequestInfo is not null) {
                var text = e.RequestInfo.ToString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            var memory = e.Memory;
            if (memory.IsEmpty) {
                return null;
            }

            return Encoding.UTF8.GetString(memory.Span);
        }

        private static async Task SendAnyAsync(object target, string payload) {
            try {
                dynamic dynamicTarget = target;

                try {
                    await dynamicTarget.SendAsync(payload).ConfigureAwait(false);
                    return;
                }
                catch {
                }

                try {
                    dynamicTarget.Send(payload);
                    return;
                }
                catch {
                }

                var bytes = Encoding.UTF8.GetBytes(payload);

                try {
                    await dynamicTarget.SendAsync(bytes).ConfigureAwait(false);
                    return;
                }
                catch {
                }

                try {
                    dynamicTarget.Send(bytes);
                    return;
                }
                catch {
                }
            }
            catch {
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

            if (head is null || string.IsNullOrWhiteSpace(head.Type)) {
                return;
            }

            switch (head.Type) {
                case "ParcelDetected": {
                        var message = JsonConvert.DeserializeObject<ParcelDetectedMessage>(json);
                        if (message != null) {
                            PublishParcelDetected(message);
                        }
                        break;
                    }

                case "SortingCompleted": {
                        var message = JsonConvert.DeserializeObject<SortingCompletedMessage>(json);
                        if (message != null) {
                            PublishSortingCompleted(message);
                        }
                        break;
                    }
            }
        }

        private void PublishDisconnected() {
            if (Volatile.Read(ref _isDisposed) == 1) {
                return;
            }

            var handlers = Disconnected;
            if (handlers is null) {
                return;
            }

            foreach (EventHandler handler in handlers.GetInvocationList()) {
                _eventDispatcher.TryDispatch(() => handler(this, EventArgs.Empty));
            }
        }

        private void PublishParcelDetected(ParcelDetectedMessage message) {
            if (Volatile.Read(ref _isDisposed) == 1) {
                return;
            }

            var handlers = ParcelDetected;
            if (handlers is null) {
                return;
            }

            foreach (EventHandler<ParcelDetectedMessage> handler in handlers.GetInvocationList()) {
                _eventDispatcher.TryDispatch(() => handler(this, message));
            }
        }

        private void PublishSortingCompleted(SortingCompletedMessage message) {
            if (Volatile.Read(ref _isDisposed) == 1) {
                return;
            }

            var handlers = SortingCompleted;
            if (handlers is null) {
                return;
            }

            foreach (EventHandler<SortingCompletedMessage> handler in handlers.GetInvocationList()) {
                _eventDispatcher.TryDispatch(() => handler(this, message));
            }
        }

        private static string Truncate(string text, int max = 500) {
            if (string.IsNullOrEmpty(text)) {
                return string.Empty;
            }

            var normalized = text.Replace("\r", " ").Replace("\n", " ");
            return normalized.Length <= max ? normalized : normalized[..max] + "...";
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
