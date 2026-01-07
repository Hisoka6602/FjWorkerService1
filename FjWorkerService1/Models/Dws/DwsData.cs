using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Models.Dws {
    public record DwsData {
        /// <summary>
        /// 条码（快递单号、运单号等）
        /// Barcode (tracking number, waybill number, etc.)
        /// </summary>
        public string Barcode { get; set; } = string.Empty;

        /// 重量（单位：克）
        /// Weight in grams
        public decimal Weight { get; set; }

        /// 长度（单位：毫米）
        /// Length in millimeters
        public decimal Length { get; set; }

        /// 宽度（单位：毫米）
        /// Width in millimeters
        public decimal Width { get; set; }

        /// 高度（单位：毫米）
        /// Height in millimeters
        public decimal Height { get; set; }

        /// 体积（单位：立方厘米）
        /// Volume in cubic centimeters
        public decimal Volume { get; set; }

        /// 扫描时间
        /// Scan timestamp
        public DateTime ScannedAt { get; set; } = DateTime.Now;
    }
}
