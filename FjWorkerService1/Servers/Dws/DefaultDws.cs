using System;
using System.Text;
using System.Threading;
using TouchSocket.Core;
using TouchSocket.Sockets;
using FjWorkerService1.Enums;
using System.Threading.Tasks;
using FjWorkerService1.Models.Conf;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace FjWorkerService1.Servers.Dws;

public sealed class DefaultDws : IDws, IDisposable {
    private readonly TcpConnectConfig _config;
    private readonly ILogger<DefaultDws> _logger;
    private readonly ConcurrentDictionary<string, object> _clients = new();
    private TcpClient? _client;
    private TcpService? _server;
    private int _connectStarted;

    private int _isConnected; // 0=false, 1=true

    public DefaultDws(TcpConnectConfig config, ILogger<DefaultDws> logger) {
        _config = config;
        _logger = logger;
    }

    public bool IsConnected => Volatile.Read(ref _isConnected) == 1;

    public event EventHandler<string>? MessageReceived;

    public event EventHandler? Disconnected;

    public void Dispose() {
        // 同步 Dispose 中不执行阻塞关闭，转为异步 best-effort
        _ = SafeDisposeAsync();
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => ConnectCoreAsync(cancellationToken);

    private async Task ConnectCoreAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _connectStarted, 1, 0) != 0) {
            return;
        }

        try {
            switch (_config.Mode) {
                case ConnectType.Client:
                    await RunClientReconnectLoopAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ConnectType.Server:
                    await StartServerAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"不支持的连接模式：{_config.Mode}");
            }
        }
        finally {
            Interlocked.Exchange(ref _connectStarted, 0);
        }
    }

    private async Task SafeDisposeAsync() {
        try {
            var client = Interlocked.Exchange(ref _client, null);
            if (client is not null) {
                try { await client.CloseAsync().ConfigureAwait(false); } catch { /* ignore */ }
                try { client.Dispose(); } catch { /* ignore */ }
            }

            var server = Interlocked.Exchange(ref _server, null);
            if (server is not null) {
                try { await server.StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
                try { server.Dispose(); } catch { /* ignore */ }
            }
        }
        finally {
            Interlocked.Exchange(ref _isConnected, 0);
        }
    }

    private async Task StartServerAsync(CancellationToken cancellationToken) {
        if (Volatile.Read(ref _server) != null) {
            return;
        }

        var service = new TcpService();

        var config = new TouchSocketConfig()
            .SetListenIPHosts([new IPHost($"{_config.Ip}:{_config.Port}")])
            .ConfigureContainer(a => { })
            .ConfigurePlugins(_ => { });

        service.Connected += OnServerClientConnected;
        service.Received += OnServerReceived;

        await service.SetupAsync(config).ConfigureAwait(false);
        await service.StartAsync(cancellationToken).ConfigureAwait(false);

        if (Interlocked.CompareExchange(ref _server, service, null) is null) {
            Interlocked.Exchange(ref _isConnected, 1);
            _logger.LogInformation("[DWS] Server started at {Ip}:{Port}", _config.Ip, _config.Port);
            return;
        }

        service.Connected -= OnServerClientConnected;
        service.Received -= OnServerReceived;
        try { await service.StopAsync().ConfigureAwait(false); } catch { }
        try { service.Dispose(); } catch { }
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
                        Interlocked.Exchange(ref _isConnected, 0);
                        _logger.LogWarning("[DWS] Client disconnected from {Ip}:{Port}", _config.Ip, _config.Port);
                        PublishDisconnected();
                    }
            }
            catch (OperationCanceledException) {
                break;
            }
            catch (Exception ex) {
                Interlocked.Exchange(ref _isConnected, 0);
                _logger.LogWarning(ex, "[DWS] Client connect failed, will retry: {Ip}:{Port}", _config.Ip, _config.Port);
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
            .SetRemoteIPHost(new IPHost($"{_config.Ip}:{_config.Port}"))
            .ConfigureContainer(a => { })
            .ConfigurePlugins(_ => { });

        client.Received += OnClientReceived;

        await client.SetupAsync(config).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (Interlocked.CompareExchange(ref _client, client, null) is null) {
            Interlocked.Exchange(ref _isConnected, 1);
            _logger.LogInformation("[DWS] Client connected to {Ip}:{Port}", _config.Ip, _config.Port);
            return;
        }

        client.Received -= OnClientReceived;
        try { await client.CloseAsync().ConfigureAwait(false); } catch { }
        try { client.Dispose(); } catch { }
    }

    private Task OnServerReceived(object client, ReceivedDataEventArgs e) {
        var msg = TryGetMessage(e);
        if (!string.IsNullOrWhiteSpace(msg)) {
            _logger.LogInformation("[DWS][RECV][SERVER] {Payload}", Truncate(msg));
            PublishMessageReceived(msg);
        }
        return Task.CompletedTask;
    }

    private Task OnClientReceived(object client, ReceivedDataEventArgs e) {
        var msg = TryGetMessage(e);
        if (!string.IsNullOrWhiteSpace(msg)) {
            _logger.LogInformation("[DWS][RECV][CLIENT] {Payload}", Truncate(msg));
            PublishMessageReceived(msg);
        }
        return Task.CompletedTask;
    }

    private void PublishDisconnected() {
        var handlers = Disconnected;
        if (handlers is null) {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList()) {
            _ = Task.Run(() => {
                try {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "[DWS] Disconnected 事件订阅者执行失败");
                }
            });
        }
    }

    private void PublishMessageReceived(string message) {
        var handlers = MessageReceived;
        if (handlers is null) {
            return;
        }

        foreach (EventHandler<string> handler in handlers.GetInvocationList()) {
            _ = Task.Run(() => {
                try {
                    handler(this, message);
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "[DWS] MessageReceived 事件订阅者执行失败");
                }
            });
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

    private static string Truncate(string s, int max = 500) {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "...";
    }

    private Task OnServerClientConnected(object client, ConnectedEventArgs e) {
        var id = TryGetClientId(client) ?? Guid.NewGuid().ToString("N");
        _clients[id] = client;
        Interlocked.Exchange(ref _isConnected, 1);
        _logger.LogInformation("[DWS][SERVER] Client connected: {ClientId}, total={Count}", id, _clients.Count);
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
}
