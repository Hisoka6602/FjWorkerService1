using FjWorkerService1.Utilities;
using FjWorkerService1.Models.Conf;
using Microsoft.Extensions.Options;

namespace FjWorkerService1.Servers {

    /// <summary>
    /// 日志清理服务 - 自动清理超过指定天数的日志文件
    /// </summary>
    public class LogCleanupService : BackgroundService {
        private readonly ILogger<LogCleanupService> _logger;
        private readonly SafeExecutor _safeExecutor;
        private readonly LogCleanupSettings _settings;

        public LogCleanupService(
            ILogger<LogCleanupService> logger,
            SafeExecutor safeExecutor,
            IOptions<LogCleanupSettings> settings) {
            _logger = logger;
            _safeExecutor = safeExecutor;
            _settings = settings.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            if (!_settings.Enabled) {
                _logger.LogInformation("日志清理服务已禁用");
                return;
            }

            _logger.LogInformation("日志清理服务已启动，保留天数: {RetentionDays}天，检查间隔: {CheckIntervalHours}小时",
                _settings.RetentionDays, _settings.CheckIntervalHours);

            // 首次启动时立即执行一次清理
            await _safeExecutor.ExecuteAsync(
                () => CleanupOldLogsAsync(),
                "首次日志清理");

            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await Task.Delay(TimeSpan.FromHours(_settings.CheckIntervalHours), stoppingToken);

                    await _safeExecutor.ExecuteAsync(
                        () => CleanupOldLogsAsync(),
                        "定期日志清理");
                }
                catch (OperationCanceledException) {
                    // 服务正在停止，正常退出
                    _logger.LogInformation("日志清理服务正在停止");
                    break;
                }
            }
        }

        private async Task CleanupOldLogsAsync() {
            var logDirectory = _settings.LogDirectory;

            // 如果是相对路径，转换为绝对路径
            if (!Path.IsPathRooted(logDirectory)) {
                logDirectory = Path.Combine(AppContext.BaseDirectory, logDirectory);
            }

            if (!Directory.Exists(logDirectory)) {
                _logger.LogWarning("日志目录不存在: {LogDirectory}", logDirectory);
                return;
            }

            var cutoffDate = DateTime.Now.AddDays(-_settings.RetentionDays);
            _logger.LogInformation("开始清理日志，删除 {CutoffDate} 之前的日志文件", cutoffDate);

            var deletedCount = 0;
            var failedCount = 0;

            // 清理日志目录中的旧文件
            var (deleted1, failed1) = await CleanupDirectoryAsync(logDirectory, cutoffDate);
            deletedCount += deleted1;
            failedCount += failed1;

            _logger.LogInformation("日志清理完成，删除文件数: {DeletedCount}，失败数: {FailedCount}",
                deletedCount, failedCount);
        }

        private async Task<(int DeletedCount, int FailedCount)> CleanupDirectoryAsync(string directory, DateTime cutoffDate) {
            return await Task.Run(() => {
                var deletedCount = 0;
                var failedCount = 0;

                try {
                    foreach (var file in Directory.EnumerateFiles(directory, "*.log", SearchOption.AllDirectories)) {
                        try {
                            var fileInfo = new FileInfo(file);
                            if (fileInfo.LastWriteTime < cutoffDate) {
                                _logger.LogInformation("删除旧日志文件: {FileName}, 最后修改时间: {LastWriteTime}",
                                    fileInfo.Name, fileInfo.LastWriteTime);

                                fileInfo.Delete();
                                deletedCount++;
                            }
                        }
                        catch (Exception ex) {
                            _logger.LogWarning(ex, "删除日志文件失败: {FileName}", file);
                            failedCount++;
                        }
                    }
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "扫描日志目录失败: {Directory}", directory);
                }

                return (deletedCount, failedCount);
            });
        }
    }
}
