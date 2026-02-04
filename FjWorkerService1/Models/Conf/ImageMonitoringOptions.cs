using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace FjWorkerService1.Models.Conf {
    /// <summary>
    /// 图片监控配置
    /// </summary>
    public sealed record class ImageMonitoringOptions {
        /// <summary>
        /// 是否启用图片新增监控
        /// </summary>
        public bool IsEnabled { get; init; } = true;

        /// <summary>
        /// 相对程序运行目录的监控目录
        /// </summary>
        public string RelativeDirectoryPath { get; init; } = "data/images";

        /// <summary>
        /// 允许的图片扩展名集合（包含点号）
        /// </summary>
        public FrozenSet<string> ImageExtensions { get; init; } =
            new[]
            {
                ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"
            }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
