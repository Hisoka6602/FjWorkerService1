using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Models.Conf {
    /// <summary>
    /// Aiduk 接口配置
    /// </summary>
    public sealed record class AidukOptions {
        /// <summary>
        /// PostCtn 接口地址
        /// </summary>
        public required string PostCtnUrl { get; set; } = string.Empty;

        /// <summary>
        /// 接口密钥（用于请求鉴权）
        /// </summary>
        public required string Secret { get; set; } = string.Empty;

        /// <summary>
        /// 设备编号
        /// </summary>
        public required int MachineId { get; set; }

        /// <summary>
        /// 请求超时时长（毫秒）
        /// </summary>
        public int TimeoutMs { get; set; } = 1000;

        /// <summary>
        /// 请求超时时长
        /// </summary>
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(TimeoutMs);
    }
}
