using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using FjWorkerService1.Models.Dws;

namespace FjWorkerService1.Models.Parcel {
    public record class ParcelInfo : DwsData {
        /// <summary>
        /// 包裹Id
        /// </summary>
        public long ParcelId { get; init; }
        /// <summary>
        /// 目标格口
        /// </summary>
        public required long ChuteId { get; init; }
        /// <summary>
        /// 实际落格Id
        /// </summary>
        public required long ActualChuteId { get; init; }
    }
}
