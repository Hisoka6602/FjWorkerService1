using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using System.Threading.Tasks;
using FjWorkerService1.Enums;
using System.Collections.Generic;
using FjWorkerService1.Models.Conf;
using FjWorkerService1.Servers.Wcs;
using Microsoft.Extensions.Options;

namespace FjWorkerService1.BackgroundServices.FileSystemImage {

    /// <summary>
    /// 文件系统图片新增监控后台服务（宿主层）
    /// </summary>
    public sealed class FileSystemImageMonitoringHostedService : BackgroundService {
        private readonly ILogger<FileSystemImageMonitoringHostedService> _logger;
        private readonly IWcs _wcs;

        private readonly ImageMonitoringOptions _options;

        private FileSystemWatcher? _watcher;

        public FileSystemImageMonitoringHostedService(
            ILogger<FileSystemImageMonitoringHostedService> logger,
            IWcs wcs,
            IOptions<ImageMonitoringOptions> options) {
            _logger = logger;
            _wcs = wcs;
            _options = options.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            if (!_options.IsEnabled) {
                _logger.LogInformation("本地图片新增监控已禁用。");
                return;
            }

            var directoryPath = _options.RelativeDirectoryPath;

            if (!Directory.Exists(directoryPath)) {
                _logger.LogWarning("监控目录不存在，将自动创建。DirectoryPath: {DirectoryPath}", directoryPath);

                try {
                    Directory.CreateDirectory(directoryPath);
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "创建监控目录失败，图片新增监控服务无法启动。DirectoryPath: {DirectoryPath}", directoryPath);
                    return;
                }
            }

            try {
                _watcher = CreateWatcher(directoryPath);
                _watcher.EnableRaisingEvents = true;

                _logger.LogInformation(
                    "本地图片新增监控已启动。DirectoryPath: {DirectoryPath}，IncludeSubdirectories: {IncludeSubdirectories}",
                    directoryPath,
                    _watcher.IncludeSubdirectories);

                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                // 正常停止
            }
            catch (Exception ex) {
                _logger.LogError(ex, "本地图片新增监控运行异常。");
            }
            finally {
                SafeDisposeWatcher();
                _logger.LogInformation("本地图片新增监控已停止。");
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken) {
            // 退出监控：释放资源，不影响��主关闭
            SafeDisposeWatcher();
            return base.StopAsync(cancellationToken);
        }

        private FileSystemWatcher CreateWatcher(string directoryPath) {
            var watcher = new FileSystemWatcher(directoryPath) {
                // 指定目录下的所有子目录都需要监控
                IncludeSubdirectories = true,

                // 仅关注新增文件，减少不必要的 IO 通知
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
            };

            watcher.Created += OnCreated;
            watcher.Error += OnError;

            return watcher;
        }

        private void OnCreated(object sender, FileSystemEventArgs e) {
            if (!IsImageFile(e.FullPath)) {
                return;
            }

            // 事件回调必须隔离异常，避免影响监控器线程
            _ = Task.Run(async () => {
                //从文件名获取条码
                if (string.IsNullOrEmpty(e.Name)) {
                    return;
                }

                var barcodeOrEmpty = ExtractBarcodeOrEmpty(e.Name);
                if (string.IsNullOrEmpty(barcodeOrEmpty)) {
                    return;
                }
                //等待文件写入完成
                await Task.Delay(1000);
                //取出文件
                var bytes = await ReadLocalFileBytesOrEmptyAsync(e.FullPath);

                //上传图片
                var uploadImageAsync = await _wcs.UploadImageAsync(barcodeOrEmpty, bytes);

                if (uploadImageAsync.RequestStatus == ApiRequestStatus.Success) {
                    _logger.LogInformation($"条码:{barcodeOrEmpty},图片：{e.Name},上传成功");
                }
                else {
                    _logger.LogError($"图片上传响应:{JsonConvert.SerializeObject(uploadImageAsync, Formatting.Indented)}");
                }
            });
        }

        private void OnError(object sender, ErrorEventArgs e) {
            // 监控器内部错误建议重建，提升可用性
            try {
                _logger.LogError(e.GetException(), "文件系统监控发生错误，将尝试重建监控器。");

                var directoryPath = _watcher?.Path;
                SafeDisposeWatcher();

                if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath)) {
                    _watcher = CreateWatcher(directoryPath);
                    _watcher.EnableRaisingEvents = true;

                    _logger.LogInformation("文件系统监控器已重建。DirectoryPath: {DirectoryPath}", directoryPath);
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "重建文件系统监控器失败。");
            }
        }

        private bool IsImageFile(string fullPath) {
            var extension = Path.GetExtension(fullPath);
            if (string.IsNullOrWhiteSpace(extension)) {
                return false;
            }

            return _options.ImageExtensions.Contains(extension);
        }

        private void SafeDisposeWatcher() {
            try {
                if (_watcher is null) {
                    return;
                }

                _watcher.EnableRaisingEvents = false;

                _watcher.Created -= OnCreated;
                _watcher.Error -= OnError;

                _watcher.Dispose();
                _watcher = null;
            }
            catch (Exception ex) {
                // Stop 阶段禁止抛出影响宿主关闭
                _logger.LogError(ex, "释放文件系统监控器资源时发生异常。");
            }
        }

        public string ExtractBarcodeOrEmpty(string fileName) {
            if (string.IsNullOrWhiteSpace(fileName)) {
                _logger.LogError("文件名为空，无法解析条码");
                return string.Empty;
            }

            try {
                var name = Path.GetFileNameWithoutExtension(fileName);
                if (string.IsNullOrWhiteSpace(name)) {
                    _logger.LogError("文件名无有效内容，无法解析条码：{FileName}", fileName);
                    return string.Empty;
                }

                // 关键点：按 '_' 分割；条码可能包含 '_'，所以用“17位全数字时间戳”当锚点
                var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) {
                    _logger.LogError("文件名段数不足，无法解析条码：{FileName}", fileName);
                    return string.Empty;
                }

                // 约定：前两段是站点信息（例：HangZhou、ZA），条码从第3段开始
                const int startIndex = 2;

                var tsIndex = -1;
                for (var i = startIndex; i < parts.Length; i++) {
                    // 时间戳锚点：长度=17 且全数字（例：20260203142240937）
                    var p = parts[i];
                    if (p.Length == 17 && p.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0) {
                        tsIndex = i;
                        break;
                    }
                }

                if (tsIndex <= startIndex) {
                    _logger.LogError("未找到17位时间戳锚点，无法解析条码：{FileName}", fileName);
                    return string.Empty;
                }

                // 条码可能包含 '_' => 把 startIndex..tsIndex-1 用 '_' 拼回去
                // 为避免 LINQ 分配，这里用 StringBuilder
                var sb = new StringBuilder(capacity: 64);
                sb.Append(parts[startIndex]);

                for (var i = startIndex + 1; i < tsIndex; i++) {
                    sb.Append('_');
                    sb.Append(parts[i]);
                }

                return sb.ToString();
            }
            catch (Exception ex) {
                _logger.LogError(ex, "解析条码发生异常：{FileName}", fileName);
                return string.Empty;
            }
        }

        public async Task<byte[]> ReadLocalFileBytesOrEmptyAsync(string filePathOrName, CancellationToken cancellationToken = default) {
            if (string.IsNullOrWhiteSpace(filePathOrName)) {
                _logger.LogError("读取本地文件失败：文件名为空");
                return [];
            }

            try {
                var fullPath = Path.IsPathRooted(filePathOrName)
                    ? filePathOrName
                    : Path.GetFullPath(filePathOrName);

                if (!File.Exists(fullPath)) {
                    _logger.LogError("读取本地文件失败：文件不存在，Path={Path}", fullPath);
                    return [];
                }

                // 说明：ReadAllBytesAsync 内部已做了高效读取；CancellationToken 可控
                return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                _logger.LogError("读取本地文件已取消：File={File}", filePathOrName);
                return [];
            }
            catch (Exception ex) {
                _logger.LogError(ex, "读取本地文件发生异常：File={File}", filePathOrName);
                return [];
            }
        }
    }
}
