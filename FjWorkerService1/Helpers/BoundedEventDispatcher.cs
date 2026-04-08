using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace FjWorkerService1.Helpers;

internal sealed class BoundedEventDispatcher : IDisposable {
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
            FullMode = BoundedChannelFullMode.DropOldest
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

        try {
            _cts.Cancel();
            _channel.Writer.TryComplete();
        }
        catch {
            // ignore
        }
        finally {
            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }
}
