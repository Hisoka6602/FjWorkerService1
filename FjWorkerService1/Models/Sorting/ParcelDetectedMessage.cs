using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Models.Sorting {

    /// <summary>
    /// 创建包裹检测消息
    /// </summary>
    public class ParcelDetectedMessage {
        public required long ParcelId { get; init; }           // 包裹ID（毫秒时间戳）
        public required DateTimeOffset DetectedAt { get; init; } // 检测时间
    }
}
