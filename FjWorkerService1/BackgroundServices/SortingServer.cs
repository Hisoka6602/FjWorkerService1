using NLog;
using System.Text;
using Newtonsoft.Json;
using System.Globalization;
using FjWorkerService1.Enums;
using FjWorkerService1.Helpers;
using FjWorkerService1.Models.Dws;
using FjWorkerService1.Models.Conf;
using FjWorkerService1.Servers.Dws;
using FjWorkerService1.Servers.Wcs;
using Microsoft.Extensions.Options;
using FjWorkerService1.Models.Parcel;
using FjWorkerService1.Models.Sorting;
using FjWorkerService1.Servers.Sorter;

namespace FjWorkerService1.BackgroundServices {

    public class SortingServer : BackgroundService {
        private static readonly TimeSpan LogCleanupInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan LogRetention = TimeSpan.FromDays(2);

        /// <summary>
        /// 关联号匹配允许误差（毫秒）
        /// 用于匹配 DWS 第 7 段关联号与 ParcelId 的时间戳差异
        /// </summary>
        private const long CorrelationIdMatchToleranceMs = 100;

        private readonly IDws _dws;
        private readonly ISorter _sorter;
        private readonly IWcs _wcs;
        private readonly ILogger<SortingServer> _logger;
        private readonly IOptionsMonitor<DataFusionOptions> _dataFusionOptions;
        private readonly string _logsDirectoryPath;
        private readonly Logger _apiLogger;

        /// <summary>
        /// 融合状态锁
        /// 所有包裹检测与 DWS 融合都在同一临界区内完成，避免并发串票
        /// </summary>
        private readonly object _fusionGate = new();

        /// <summary>
        /// 全量包裹索引
        /// 已绑定、未绑定、待回调的包裹都保留在这里
        /// </summary>
        private readonly Dictionary<long, ParcelInfo> _parcelInfos = new();

        /// <summary>
        /// 待融合包裹顺序队列
        /// 只存尚未绑定 DWS 的包裹，严格按检测顺序维护
        /// </summary>
        private readonly LinkedList<long> _pendingParcelOrder = new();

        /// <summary>
        /// 待融合包裹节点索引
        /// 便于 O(1) 删除
        /// </summary>
        private readonly Dictionary<long, LinkedListNode<long>> _pendingParcelNodes = new();

        /// <summary>
        /// 暂存未命中的 DWS 队列
        /// 用于处理“DWS 先到、包裹后到”以及事件顺序抖动场景
        /// </summary>
        private readonly LinkedList<DwsInboundMessage> _pendingDwsOrder = new();

        public SortingServer(
            IDws dws,
            ISorter sorter,
            IWcs wcs,
            ILogger<SortingServer> logger,
            IHostEnvironment env,
            IOptionsMonitor<DataFusionOptions> dataFusionOptions) {
            _logsDirectoryPath = Path.Combine(env.ContentRootPath, "logs");
            _dws = dws;
            _sorter = sorter;
            _wcs = wcs;
            _logger = logger;
            _dataFusionOptions = dataFusionOptions;
            _apiLogger = LogManager.GetLogger("FjWorkerService1.Servers.Wcs.SortingServerApi");

            _sorter.Disconnected += (_, _) => {
                _logger.LogWarning("分拣程序断开连接");
            };

            _sorter.ParcelDetected += async (_, message) => {
                try {
                    MatchedParcelWorkItem? workItem;
                    ParcelInfo storedParcelInfo;

                    lock (_fusionGate) {
                        var now = DateTimeOffset.Now;
                        CleanupExpiredStateLocked(now);

                        storedParcelInfo = UpsertDetectedParcelLocked(message);
                        workItem = TryBindPendingDwsForParcelLocked(storedParcelInfo.ParcelId, now);
                    }

                    _logger.LogInformation("检测到包裹: {Payload}", JsonConvert.SerializeObject(storedParcelInfo));

                    if (workItem is not null) {
                        await HandleMatchedParcelAsync(workItem).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "处理包裹检测事件发生异常");
                }
            };

            _sorter.SortingCompleted += async (_, message) => {
                try {
                    ParcelInfo? removedParcelInfo = null;

                    lock (_fusionGate) {
                        RemovePendingParcelLocked(message.ParcelId);

                        if (_parcelInfos.Remove(message.ParcelId, out var parcelInfo)) {
                            removedParcelInfo = parcelInfo;
                        }
                    }

                    if (removedParcelInfo is not null) {
                        await _wcs.NotifyChuteLandingAsync(
                                removedParcelInfo.ParcelId,
                                message.ActualChuteId.ToString(),
                                removedParcelInfo.Barcode)
                            .ConfigureAwait(false);

                        _logger.LogInformation("落格回调:{Payload}", JsonConvert.SerializeObject(message));
                    }
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "处理落格回调发生异常");
                }
            };

            _dws.MessageReceived += async (_, rawMessage) => {
                try {
                    _logger.LogInformation("接收到DWS内容:{Payload}", rawMessage);

                    if (!TryParseDwsInboundMessage(rawMessage, out var dwsMessage, out var parseError)) {
                        _logger.LogWarning("DWS 内容解析失败，原始内容={Payload}，原因={Reason}", rawMessage, parseError);
                        return;
                    }

                    MatchedParcelWorkItem? workItem;

                    lock (_fusionGate) {
                        var now = DateTimeOffset.Now;
                        CleanupExpiredStateLocked(now);

                        workItem = TryMatchParcelForDwsLocked(dwsMessage!, now);
                        if (workItem is null) {
                            _pendingDwsOrder.AddLast(dwsMessage!);
                        }
                    }

                    if (workItem is not null) {
                        await HandleMatchedParcelAsync(workItem).ConfigureAwait(false);
                        return;
                    }

                    _logger.LogWarning(
                        "当前没有可融合包裹，DWS 已暂存。barcode={Barcode} correlationId={CorrelationId}",
                        dwsMessage!.Barcode,
                        dwsMessage.CorrelationId?.ToString() ?? "null");
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "融合流程发生异常");
                }
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            TryStartConnectOnce(stoppingToken);

            await SafeCleanupLogsAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(LogCleanupInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                await SafeCleanupLogsAsync(stoppingToken).ConfigureAwait(false);

                lock (_fusionGate) {
                    CleanupExpiredStateLocked(DateTimeOffset.Now);
                    RemoveCompletedRetentionLocked(DateTimeOffset.Now);
                }
            }
        }

        private void TryStartConnectOnce(CancellationToken stoppingToken) {
            try {
                _dws.ConnectAsync(stoppingToken)
                    .Observe(_logger, "DWS 连接任务");

                _sorter.ConnectAsync(stoppingToken)
                    .Observe(_logger, "Sorter 连接任务");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "连接任务启动失败");
            }
        }

        private ParcelInfo UpsertDetectedParcelLocked(ParcelDetectedMessage message) {
            var detectedAt = message.DetectedAt.LocalDateTime;

            if (_parcelInfos.TryGetValue(message.ParcelId, out var existingParcelInfo)) {
                if (existingParcelInfo.IsDwsBound) {
                    return existingParcelInfo;
                }

                var refreshedParcelInfo = existingParcelInfo with {
                    ScannedAt = detectedAt
                };

                _parcelInfos[message.ParcelId] = refreshedParcelInfo;

                if (!_pendingParcelNodes.ContainsKey(message.ParcelId)) {
                    _pendingParcelNodes[message.ParcelId] = _pendingParcelOrder.AddLast(message.ParcelId);
                }

                return refreshedParcelInfo;
            }

            var parcelInfo = new ParcelInfo {
                ParcelId = message.ParcelId,
                ChuteId = 0,
                ActualChuteId = 0,
                IsDwsBound = false,
                ScannedAt = detectedAt
            };

            _parcelInfos[message.ParcelId] = parcelInfo;
            _pendingParcelNodes[message.ParcelId] = _pendingParcelOrder.AddLast(message.ParcelId);

            return parcelInfo;
        }

        private MatchedParcelWorkItem? TryBindPendingDwsForParcelLocked(long parcelId, DateTimeOffset now) {
            if (!_parcelInfos.TryGetValue(parcelId, out var parcelInfo) || parcelInfo.IsDwsBound) {
                return null;
            }

            LinkedListNode<DwsInboundMessage>? matchedNode = null;
            var bestDelta = long.MaxValue;

            for (var node = _pendingDwsOrder.First; node is not null; node = node.Next) {
                var correlationId = node.Value.CorrelationId;
                if (!correlationId.HasValue) {
                    continue;
                }

                var delta = Math.Abs(correlationId.Value - parcelId);
                if (delta > CorrelationIdMatchToleranceMs) {
                    continue;
                }

                if (delta < bestDelta) {
                    bestDelta = delta;
                    matchedNode = node;
                }
            }

            if (matchedNode is null) {
                return null;
            }

            var dwsMessage = matchedNode.Value;
            _pendingDwsOrder.Remove(matchedNode);

            return BindParcelLocked(parcelId, dwsMessage, now);
        }

        private MatchedParcelWorkItem? TryMatchParcelForDwsLocked(DwsInboundMessage dwsMessage, DateTimeOffset now) {
            if (dwsMessage.CorrelationId.HasValue &&
                TryFindNearestPendingParcelByCorrelationLocked(dwsMessage.CorrelationId.Value, out var matchedParcelId)) {
                return BindParcelLocked(matchedParcelId, dwsMessage, now);
            }

            if (TryGetOldestPendingParcelLocked(out var oldestParcelId)) {
                return BindParcelLocked(oldestParcelId, dwsMessage, now);
            }

            return null;
        }

        private bool TryFindNearestPendingParcelByCorrelationLocked(long correlationId, out long parcelId) {
            parcelId = default;
            var bestDelta = long.MaxValue;

            for (var node = _pendingParcelOrder.First; node is not null; node = node.Next) {
                var currentParcelId = node.Value;
                var delta = Math.Abs(currentParcelId - correlationId);
                if (delta > CorrelationIdMatchToleranceMs) {
                    continue;
                }

                if (delta < bestDelta) {
                    bestDelta = delta;
                    parcelId = currentParcelId;
                }
            }

            return bestDelta != long.MaxValue;
        }

        private bool TryGetOldestPendingParcelLocked(out long parcelId) {
            parcelId = default;

            var node = _pendingParcelOrder.First;
            while (node is not null) {
                if (_parcelInfos.TryGetValue(node.Value, out var parcelInfo) && !parcelInfo.IsDwsBound) {
                    parcelId = node.Value;
                    return true;
                }

                var current = node;
                node = node.Next;
                _pendingParcelOrder.Remove(current);
            }

            return false;
        }

        private MatchedParcelWorkItem BindParcelLocked(long parcelId, DwsInboundMessage dwsMessage, DateTimeOffset now) {
            var parcelInfo = _parcelInfos[parcelId];

            var updatedParcelInfo = parcelInfo with {
                Barcode = dwsMessage.Barcode,
                Weight = dwsMessage.Weight,
                Length = dwsMessage.Length,
                Width = dwsMessage.Width,
                Height = dwsMessage.Height,
                Volume = dwsMessage.Volume,
                IsDwsBound = true
            };

            _parcelInfos[parcelId] = updatedParcelInfo;
            RemovePendingParcelLocked(parcelId);

            return new MatchedParcelWorkItem(updatedParcelInfo, dwsMessage, now);
        }

        private void RemovePendingParcelLocked(long parcelId) {
            if (_pendingParcelNodes.TryGetValue(parcelId, out var node)) {
                _pendingParcelOrder.Remove(node);
                _pendingParcelNodes.Remove(parcelId);
                return;
            }

            for (var current = _pendingParcelOrder.First; current is not null; current = current.Next) {
                if (current.Value != parcelId) {
                    continue;
                }

                _pendingParcelOrder.Remove(current);
                break;
            }
        }

        private void CleanupExpiredStateLocked(DateTimeOffset now) {
            var timeoutDuration = _dataFusionOptions.CurrentValue.TimeoutDuration;
            var timeoutThreshold = now.LocalDateTime - timeoutDuration;

            while (_pendingParcelOrder.First is not null) {
                var parcelId = _pendingParcelOrder.First.Value;
                if (!_parcelInfos.TryGetValue(parcelId, out var parcelInfo)) {
                    _pendingParcelOrder.RemoveFirst();
                    _pendingParcelNodes.Remove(parcelId);
                    continue;
                }

                if (parcelInfo.IsDwsBound || parcelInfo.ScannedAt >= timeoutThreshold) {
                    break;
                }

                _pendingParcelOrder.RemoveFirst();
                _pendingParcelNodes.Remove(parcelId);

                _logger.LogWarning(
                    "包裹等待 DWS 超时，已移出待融合队列。parcelId={ParcelId} scannedAt={ScannedAt:O}",
                    parcelId,
                    parcelInfo.ScannedAt);
            }

            while (_pendingDwsOrder.First is not null) {
                var dwsMessage = _pendingDwsOrder.First.Value;
                if (dwsMessage.ReceivedAt.LocalDateTime >= timeoutThreshold) {
                    break;
                }

                _pendingDwsOrder.RemoveFirst();

                _logger.LogWarning(
                    "DWS 等待包裹超时，已丢弃。barcode={Barcode} correlationId={CorrelationId} receivedAt={ReceivedAt:O}",
                    dwsMessage.Barcode,
                    dwsMessage.CorrelationId?.ToString() ?? "null",
                    dwsMessage.ReceivedAt);
            }
        }

        private void RemoveCompletedRetentionLocked(DateTimeOffset now) {
            var keysToRemove = new List<long>();

            foreach (var item in _parcelInfos) {
                if (!item.Value.IsDwsBound) {
                    continue;
                }

                if (now.LocalDateTime.Subtract(item.Value.ScannedAt).TotalHours <= 1) {
                    continue;
                }

                keysToRemove.Add(item.Key);
            }

            foreach (var key in keysToRemove) {
                _parcelInfos.Remove(key);
                RemovePendingParcelLocked(key);
            }
        }

        private async Task HandleMatchedParcelAsync(MatchedParcelWorkItem workItem) {
            try {
                var scannedAt = workItem.DwsMessage.ReceivedAt.LocalDateTime;

                var requestChuteResponse = await _wcs.RequestChuteAsync(
                        workItem.ParcelInfo.ParcelId,
                        new DwsData {
                            Barcode = workItem.ParcelInfo.Barcode,
                            Weight = workItem.ParcelInfo.Weight,
                            Length = workItem.ParcelInfo.Length,
                            Width = workItem.ParcelInfo.Width,
                            Height = workItem.ParcelInfo.Height,
                            Volume = workItem.ParcelInfo.Volume,
                            ScannedAt = scannedAt
                        })
                    .ConfigureAwait(false);

                LogApiResponseSummary(
                    "RequestChute",
                    requestChuteResponse,
                    workItem.ParcelInfo.ParcelId,
                    workItem.ParcelInfo.Barcode);

                if (string.IsNullOrWhiteSpace(requestChuteResponse.ResponseBody)) {
                    _logger.LogWarning("上传分拣数据失败:{Payload}", JsonConvert.SerializeObject(requestChuteResponse));
                    return;
                }

                ChuteIdParser.TryParseChuteNumber(requestChuteResponse.ResponseBody, out var chuteId);

                lock (_fusionGate) {
                    if (_parcelInfos.TryGetValue(workItem.ParcelInfo.ParcelId, out var currentParcelInfo)) {
                        _parcelInfos[workItem.ParcelInfo.ParcelId] = currentParcelInfo with {
                            ChuteId = chuteId
                        };
                    }
                }

                var chuteAssignmentEventArgs = new ChuteAssignmentEventArgs {
                    ParcelId = workItem.ParcelInfo.ParcelId,
                    ChuteId = chuteId,
                    DwsPayload = new DwsMeasurement {
                        WeightGrams = workItem.ParcelInfo.Weight,
                        LengthMm = workItem.ParcelInfo.Length,
                        WidthMm = workItem.ParcelInfo.Width,
                        HeightMm = workItem.ParcelInfo.Height,
                        VolumetricWeightGrams = workItem.ParcelInfo.Volume,
                        Barcode = workItem.ParcelInfo.Barcode,
                        MeasuredAt = workItem.DwsMessage.ReceivedAt
                    },
                    AssignedAt = DateTimeOffset.Now
                };

                await _sorter.SendEventAsync(chuteAssignmentEventArgs).ConfigureAwait(false);

                _logger.LogInformation(
                    "已发送目标格口数据:{Payload}",
                    JsonConvert.SerializeObject(chuteAssignmentEventArgs, Formatting.Indented));
            }
            catch (Exception ex) {
                _logger.LogError(ex, "处理已匹配包裹失败。parcelId={ParcelId}", workItem.ParcelInfo.ParcelId);
            }
        }

        private static bool TryParseDwsInboundMessage(
            string rawMessage,
            out DwsInboundMessage? dwsMessage,
            out string errorMessage) {
            dwsMessage = null;
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(rawMessage)) {
                errorMessage = "DWS 原始内容为空";
                return false;
            }

            var split = rawMessage
                .Split(',', StringSplitOptions.TrimEntries)
                .ToArray();

            if (split.Length == 0 || string.IsNullOrWhiteSpace(split[0])) {
                errorMessage = "DWS 条码为空";
                return false;
            }

            var barcode = split[0];
            var weight = TryParseDecimal(split, 1);
            var length = TryParseDecimal(split, 2);
            var width = TryParseDecimal(split, 3);
            var height = TryParseDecimal(split, 4);
            var volume = TryParseDecimal(split, 5);
            var correlationId = TryParseLong(split, 6);

            dwsMessage = new DwsInboundMessage {
                RawMessage = rawMessage,
                Barcode = barcode,
                Weight = weight,
                Length = length,
                Width = width,
                Height = height,
                Volume = volume,
                CorrelationId = correlationId,
                ReceivedAt = DateTimeOffset.Now
            };

            return true;
        }

        private static decimal TryParseDecimal(IReadOnlyList<string> values, int index) {
            if (index >= values.Count) {
                return 0m;
            }

            return decimal.TryParse(
                values[index],
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var value)
                ? value
                : 0m;
        }

        private static long? TryParseLong(IReadOnlyList<string> values, int index) {
            if (index >= values.Count) {
                return null;
            }

            return long.TryParse(values[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private async Task SafeCleanupLogsAsync(CancellationToken stoppingToken) {
            try {
                var threshold = DateTime.Now - LogRetention;
                var scannedCount = 0;
                var deletedCount = 0;

                if (!Directory.Exists(_logsDirectoryPath)) {
                    _logger.LogInformation("日志目录不存在，已跳过清理：{Path}", _logsDirectoryPath);
                    return;
                }

                foreach (var filePath in Directory.EnumerateFiles(_logsDirectoryPath, "*.log", SearchOption.AllDirectories)) {
                    stoppingToken.ThrowIfCancellationRequested();

                    scannedCount++;

                    try {
                        var lastWrite = File.GetLastWriteTime(filePath);
                        if (lastWrite >= threshold) {
                            continue;
                        }

                        File.Delete(filePath);
                        deletedCount++;
                    }
                    catch (IOException ex) {
                        _logger.LogWarning(ex, "日志文件删除失败（IO异常），已跳过：{File}", filePath);
                    }
                    catch (UnauthorizedAccessException ex) {
                        _logger.LogWarning(ex, "日志文件删除失败（权限不足），已跳过：{File}", filePath);
                    }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "日志文件删除失败（未知异常），已跳过：{File}", filePath);
                    }
                }

                _logger.LogInformation(
                    "日志清理完成，扫描文件数={Scanned}，删除文件数={Deleted}，阈值时间(UTC)={Threshold}，目录={Path}",
                    scannedCount,
                    deletedCount,
                    threshold,
                    _logsDirectoryPath);

                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            }
            catch (Exception ex) {
                _logger.LogError(ex, "日志清理任务发生异常");
            }
        }

        private void LogApiResponseSummary(
            string operation,
            Models.Wcs.WcsApiResponse response,
            long parcelId,
            string? barcode) {
            var statusCode = response.ResponseStatusCode?.ToString() ?? "-";
            var message = Truncate(response.FormattedMessage ?? response.ErrorMessage ?? string.Empty, 300);
            var responsePreview = Truncate(response.ResponseBody ?? string.Empty, 300);

            if (response.RequestStatus == ApiRequestStatus.Success) {
                _apiLogger.Info(
                    "[Api][{Operation}] success parcelId={ParcelId} barcode={Barcode} statusCode={StatusCode} durationMs={DurationMs} message={Message}",
                    operation,
                    parcelId,
                    barcode ?? string.Empty,
                    statusCode,
                    response.DurationMs,
                    message);
                return;
            }

            if (response.RequestStatus == ApiRequestStatus.Exception) {
                _apiLogger.Error(
                    "[Api][{Operation}] exception parcelId={ParcelId} barcode={Barcode} statusCode={StatusCode} durationMs={DurationMs} message={Message} response={ResponsePreview}",
                    operation,
                    parcelId,
                    barcode ?? string.Empty,
                    statusCode,
                    response.DurationMs,
                    message,
                    responsePreview);
                return;
            }

            _apiLogger.Warn(
                "[Api][{Operation}] failed parcelId={ParcelId} barcode={Barcode} statusCode={StatusCode} durationMs={DurationMs} message={Message} response={ResponsePreview}",
                operation,
                parcelId,
                barcode ?? string.Empty,
                statusCode,
                response.DurationMs,
                message,
                responsePreview);
        }

        private static string Truncate(string text, int maxLength) {
            if (string.IsNullOrWhiteSpace(text)) {
                return string.Empty;
            }

            var normalized = text.Replace("\r", " ").Replace("\n", " ");
            return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
        }

        private sealed record class DwsInboundMessage {
            public required string RawMessage { get; init; }
            public required string Barcode { get; init; }
            public required decimal Weight { get; init; }
            public required decimal Length { get; init; }
            public required decimal Width { get; init; }
            public required decimal Height { get; init; }
            public required decimal Volume { get; init; }
            public required DateTimeOffset ReceivedAt { get; init; }
            public long? CorrelationId { get; init; }
        }

        private sealed record class MatchedParcelWorkItem {
            public MatchedParcelWorkItem(
                ParcelInfo parcelInfo,
                DwsInboundMessage dwsMessage,
                DateTimeOffset matchedAt) {
                ParcelInfo = parcelInfo;
                DwsMessage = dwsMessage;
                MatchedAt = matchedAt;
            }

            public ParcelInfo ParcelInfo { get; }
            public DwsInboundMessage DwsMessage { get; }
            public DateTimeOffset MatchedAt { get; }
        }
    }
}
