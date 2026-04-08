using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace FjWorkerService1.Helpers;

internal sealed class BoundedEventDispatcher : IDisposable {
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Action> _channel;
    private readonly Task[] _workers;
    private int _isDisposed;

    public BoundedEventDispatcher(
        ILogger logger,
        string name,
        int capacity = 2048,
        int workers = 4) {
        _logger = logger;
        _name = name;

        var bounded = new BoundedChannelOptions(capacity) {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        };
        _channel = Channel.CreateBounded<Action>(bounded);

        var workerCount = Math.Clamp(workers, 1, Environment.ProcessorCount);
        _workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++) {
            _workers[i] = Task.Run(ProcessLoopAsync);
        }
    }

    public bool TryDispatch(Action action) {
        if (Volatile.Read(ref _isDisposed) == 1) {
            return false;
        }

        if (_cts.IsCancellationRequested) {
            return false;
        }

        if (_channel.Writer.TryWrite(action)) {
            return true;
        }

        _logger.LogWarning("[{Dispatcher}] 事件分发队列繁忙，事件已丢弃。", _name);
        return false;
    }

    private async Task ProcessLoopAsync() {
        var token = _cts.Token;
        try {
            while (await _channel.Reader.WaitToReadAsync(token).ConfigureAwait(false)) {
                while (_channel.Reader.TryRead(out var action)) {
                    try {
                        action();
                    }
                    catch (Exception ex) {
                        _logger.LogError(ex, "[{Dispatcher}] 事件订阅者执行失败。", _name);
                    }
                }
            }
        }
        catch (OperationCanceledException) {
            // ignore
        }
        catch (Exception ex) {
            _logger.LogError(ex, "[{Dispatcher}] 事件分发循环异常退出。", _name);
        }
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1) {
            return;
        }

        var cancelled = false;
        try {
            _channel.Writer.TryComplete();
            if (Task.WaitAll(_workers, DisposeDrainTimeout) is false) {
                _logger.LogWarning("[{Dispatcher}] 事件分发器停止超时，尝试强制取消剩余工作。", _name);
                _cts.Cancel();
                cancelled = true;
                Task.WaitAll(_workers, TimeSpan.FromSeconds(1));
            }
        }
        catch {
            // ignore
            if (!cancelled) {
                try {
                    _cts.Cancel();
                    cancelled = true;
                }
                catch {
                    // ignore
                }
            }
        }
        finally {
            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }
}
