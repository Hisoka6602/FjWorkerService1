using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Models.Conf {
    /// <summary>
    /// 数据融合配置
    /// </summary>
    public sealed record class DataFusionOptions {
        /// <summary>
        /// 数据融合超时时间（毫秒）
        /// </summary>
        public int Timeout { get; set; } = 1000;

        /// <summary>
        /// 数据融合超时时长
        /// </summary>
        public TimeSpan TimeoutDuration => TimeSpan.FromMilliseconds(Timeout);
    }
}
