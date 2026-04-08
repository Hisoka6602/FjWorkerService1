using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using System.Threading.Tasks;
using FjWorkerService1.Enums;
using FjWorkerService1.Helpers;
using System.Collections.Generic;
using FjWorkerService1.Models.Dws;
using FjWorkerService1.Servers.Dws;
using FjWorkerService1.Servers.Wcs;
using FjWorkerService1.Models.Conf;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using FjWorkerService1.Models.Parcel;
using FjWorkerService1.Models.Sorting;
using FjWorkerService1.Servers.Sorter;

namespace FjWorkerService1.BackgroundServices {

    public class SortingServer : BackgroundService {
        private readonly IDws _dws;
        private readonly ISorter _sorter;
        private readonly IWcs _wcs;
        private readonly ILogger<SortingServer> _logger;
        private readonly IOptionsMonitor<DataFusionOptions> _dataFusionOptions;
        private readonly string _logsDirectoryPath;
        private static readonly TimeSpan LogCleanupInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan LogRetention = TimeSpan.FromDays(2);
        private const int ParcelInfoUpdateMaxAttempts = 64;

        //先进先出包裹队列
        private ConcurrentDictionary<long, ParcelInfo> _parcelInfos = new();

        public SortingServer(IDws dws, ISorter sorter, IWcs wcs,
            ILogger<SortingServer> logger, IHostEnvironment env,
            IOptionsMonitor<DataFusionOptions> dataFusionOptions) {
            _logsDirectoryPath = Path.Combine(env.ContentRootPath, "logs");
            _dws = dws;
            _sorter = sorter;
            _wcs = wcs;
            _logger = logger;
            _dataFusionOptions = dataFusionOptions;

            _sorter.Disconnected += (sender, args) => {
                _logger.LogWarning($"分拣程序断开连接");
            };
            _sorter.ParcelDetected += (sender, message) => {
                var parcelInfo = new ParcelInfo {
                    ParcelId = message.ParcelId,
                    ChuteId = 0,
                    ActualChuteId = 0,
                    IsDwsBound = false,
                };
                var storedParcelInfo = _parcelInfos.AddOrUpdate(
                    message.ParcelId,
                    parcelInfo,
                    (_, existingParcelInfo) => existingParcelInfo.IsDwsBound ? existingParcelInfo : parcelInfo);
                _logger.LogInformation($"检测到包裹: {JsonConvert.SerializeObject(storedParcelInfo)}");
            };
            _sorter.SortingCompleted += async (sender, message) => {
                await Task.Yield();
                if (_parcelInfos.TryRemove(message.ParcelId, out var value)) {
                    await _wcs.NotifyChuteLandingAsync(value.ParcelId, message.ActualChuteId.ToString(), value.Barcode);

                    _logger.LogInformation($"落格回调:{JsonConvert.SerializeObject(message)}");
                }
            };
            _dws.MessageReceived += async (sender, s) => {
                try {
                    _logger.LogInformation($"接收到DWS内容:{s}");
                    var split = s.Split(",");
                    if (split.Length > 0) {
                        // 取出并原子更新包裹，避免并发回调对同一对象竞争写入
                        ParcelInfo? value = null;
                        var updateRetriesExhausted = false;
                        for (var attempt = 0; attempt < ParcelInfoUpdateMaxAttempts; attempt++) {
                            var now = DateTime.Now;
                            var candidate = _parcelInfos.FirstOrDefault(f =>
                                !f.Value.IsDwsBound
                                && now.Subtract(f.Value.ScannedAt).TotalMilliseconds < _dataFusionOptions.CurrentValue.Timeout);
                            if (candidate.Value is null) {
                                break;
                            }

                            var updated = candidate.Value with {
                                Barcode = split[0],
                                Weight = split.Length > 1 ? Convert.ToDecimal(split[1]) : candidate.Value.Weight,
                                Length = split.Length > 5 ? Convert.ToDecimal(split[2]) : candidate.Value.Length,
                                Width = split.Length > 5 ? Convert.ToDecimal(split[3]) : candidate.Value.Width,
                                Height = split.Length > 5 ? Convert.ToDecimal(split[4]) : candidate.Value.Height,
                                Volume = split.Length > 5 ? Convert.ToDecimal(split[5]) : candidate.Value.Volume,
                                IsDwsBound = true
                            };

                            if (_parcelInfos.TryUpdate(candidate.Key, updated, candidate.Value)) {
                                value = updated;
                                break;
                            }

                            if (attempt == ParcelInfoUpdateMaxAttempts - 1) {
                                updateRetriesExhausted = true;
                            }

                            await Task.Yield();
                        }

                        if (updateRetriesExhausted) {
                            _logger.LogWarning("并发更新包裹信息达到重试上限，已放弃本次DWS融合。");
                        }

                        if (value is not null) {
                            //上传
                            var requestChuteAsync = await _wcs.RequestChuteAsync(value.ParcelId, new DwsData() {
                                Barcode = value.Barcode,
                                Weight = value.Weight,
                                Length = value.Length,
                                Width = value.Width,
                                Height = value.Height,
                                Volume = value.Volume,
                                ScannedAt = DateTime.Now
                            });
                            LogApiResponseSummary("RequestChute", requestChuteAsync, value.ParcelId, value.Barcode);

                            if (!string.IsNullOrEmpty(requestChuteAsync.ResponseBody)) {
                                ChuteIdParser.TryParseChuteNumber(requestChuteAsync.ResponseBody, out var chuteId);
                                var chuteAssignmentEventArgs = new ChuteAssignmentEventArgs {
                                    ParcelId = value.ParcelId,
                                    ChuteId = chuteId,
                                    DwsPayload = new DwsMeasurement {
                                        WeightGrams = value.Weight,
                                        LengthMm = value.Length,
                                        WidthMm = value.Width,
                                        HeightMm = value.Height,
                                        VolumetricWeightGrams = value.Volume,
                                        Barcode = value.Barcode,
                                        MeasuredAt = DateTime.Now
                                    },
                                    AssignedAt = DateTime.Now
                                };
                                await _sorter.SendEventAsync(chuteAssignmentEventArgs);
                                _logger.LogInformation($"已发送目标格口数据:{JsonConvert.SerializeObject(chuteAssignmentEventArgs, Formatting.Indented)}");
                            }
                            else {
                                _logger.LogWarning($"上传分拣数据失败:{JsonConvert.SerializeObject(requestChuteAsync)}");
                            }
                        }
                        else {
                            _logger.LogError($"获取不到包裹,无法融合DWS数据");
                        }
                    }
                }
                catch (Exception e) {
                    _logger.LogError(e, "融合流程发生异常");
                }
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            // 仅触发一次连接：异常隔离并记录，避免静默失败
            TryStartConnectOnce(stoppingToken);

            // 首次立即清理一次，随后每小时清理一次
            await SafeCleanupLogsAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(LogCleanupInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                await SafeCleanupLogsAsync(stoppingToken).ConfigureAwait(false);
                _parcelInfos.RemoveWhen(w =>
                    DateTime.Now.Subtract(DateTimeOffset.FromUnixTimeMilliseconds(w.Key).DateTime).TotalHours > 1);
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
                // 仅捕获启动阶段的同步异常，避免后台服务被直接打断
                _logger.LogError(ex, "连接任务启动失败");
            }
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
                // 服务关闭时无需记录为错误
            }
            catch (Exception ex) {
                _logger.LogError(ex, "日志清理任务发生异常");
            }
        }

        private void LogApiResponseSummary(string operation, Models.Wcs.WcsApiResponse response, long parcelId, string? barcode) {
            var statusCode = response.ResponseStatusCode?.ToString() ?? "-";
            var message = Truncate(response.FormattedMessage ?? response.ErrorMessage ?? string.Empty, 300);
            var responsePreview = Truncate(response.ResponseBody ?? string.Empty, 300);

            if (response.RequestStatus == ApiRequestStatus.Success) {
                _logger.LogInformation(
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
                _logger.LogError(
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

            _logger.LogWarning(
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
    }
}
