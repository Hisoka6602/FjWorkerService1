using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Models.Dws {
    public readonly record struct DwsMeasurement {
        /// <summary>
        /// 重量（克）
        /// </summary>
        public decimal WeightGrams { get; init; }

        /// <summary>
        /// 长度（毫米）
        /// </summary>
        public decimal LengthMm { get; init; }

        /// <summary>
        /// 宽度（毫米）
        /// </summary>
        public decimal WidthMm { get; init; }

        /// <summary>
        /// 高度（毫米）
        /// </summary>
        public decimal HeightMm { get; init; }

        /// <summary>
        /// 体积重量（克），可选，由上游计算
        /// </summary>
        public decimal? VolumetricWeightGrams { get; init; }

        /// <summary>
        /// 条码（可选）
        /// </summary>
        public string? Barcode { get; init; }

        /// <summary>
        /// 测量时间
        /// </summary>
        public DateTimeOffset MeasuredAt { get; init; }
    }
}
