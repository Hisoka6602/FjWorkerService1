using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using System.Threading.Tasks;
using FjWorkerService1.Helpers;
using System.Collections.Generic;
using FjWorkerService1.Models.Dws;
using FjWorkerService1.Servers.Dws;
using FjWorkerService1.Servers.Wcs;
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
        private readonly string _logsDirectoryPath;
        private static readonly TimeSpan LogCleanupInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan LogRetention = TimeSpan.FromDays(2);

        //先进先出包裹队列
        private ConcurrentDictionary<long, ParcelInfo> _parcelInfos = new();

        public SortingServer(IDws dws, ISorter sorter, IWcs wcs, ILogger<SortingServer> logger, IHostEnvironment env) {
            _logsDirectoryPath = Path.Combine(env.ContentRootPath, "logs");
            _dws = dws;
            _sorter = sorter;
            _wcs = wcs;
            _logger = logger;

            _sorter.Disconnected += (sender, args) => {
                _logger.LogWarning($"分拣程序断开连接");
            };
            _sorter.ParcelDetected += (sender, message) => {
                var parcelInfo = new ParcelInfo {
                    ParcelId = message.ParcelId,

                    ChuteId = 0,
                    ActualChuteId = 0,
                };
                _parcelInfos.TryAdd(message.ParcelId, parcelInfo);
                _logger.LogInformation($"检测到包裹: {JsonConvert.SerializeObject(parcelInfo)}");
            };
            _sorter.SortingCompleted += async (sender, message) => {
                await Task.Yield();
                var (key, value) = _parcelInfos.FirstOrDefault(w =>
                    w.Key.Equals(message.ParcelId));
                if (value is not null) {
                    await _wcs.NotifyChuteLandingAsync(value.ParcelId, message.ActualChuteId.ToString(),
                        value.Barcode);

                    _logger.LogInformation($"落格回调:{JsonConvert.SerializeObject(message)}");
                    _parcelInfos.Remove(key, out var _);
                }
            };
            _dws.MessageReceived += async (sender, s) => {
                var split = s.Split(",");
                if (split.Length > 1) {
                    //取出包裹
                    var (key, value) = _parcelInfos.FirstOrDefault(f =>
                        string.IsNullOrEmpty(f.Value.Barcode) && DateTime.Now.Subtract(f.Value.ScannedAt).TotalMilliseconds < 2000);
                    if (value is not null) {
                        //赋值
                        value.Barcode = split[0];
                        //上传
                        var requestChuteAsync = await _wcs.RequestChuteAsync(value.ParcelId, new DwsData() {
                            Barcode = value.Barcode,
                            ScannedAt = DateTime.Now
                        });

                        if (!string.IsNullOrEmpty(requestChuteAsync.ResponseBody)) {
                            ChuteIdParser.TryParseChuteNumber(requestChuteAsync.ResponseBody, out var chuteId);

                            await _sorter.SendEventAsync(new ChuteAssignmentEventArgs {
                                ParcelId = value.ParcelId,
                                ChuteId = chuteId,
                                DwsPayload = new DwsMeasurement {
                                    WeightGrams = 0,
                                    LengthMm = 0,
                                    WidthMm = 0,
                                    HeightMm = 0,
                                    VolumetricWeightGrams = null,
                                    Barcode = value.Barcode,
                                    MeasuredAt = DateTime.Now
                                },
                                AssignedAt = DateTime.Now
                            });
                        }
                        else {
                            _logger.LogWarning($"上传分拣数据失败:{JsonConvert.SerializeObject(requestChuteAsync)}");
                        }
                    }
                    else {
                        _logger.LogError($"获取不到包裹,无法融合DWS数据");
                    }
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
    }
}
