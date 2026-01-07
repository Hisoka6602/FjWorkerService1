using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Models.Conf {

    public class PostProcessingCenterConfig {

        /// <summary>
        /// API接口URL
        /// API endpoint URL
        /// </summary>
        /// <example>http://10.4.188.85/pcs-tc-nc-job/WyService/services/CommWY?wsdl</example>
        public required string Url { get; init; } = "http://10.4.188.85/pcs-tc-nc-job/WyService/services/CommWY";

        /// <summary>
        /// 超时时间 (毫秒)
        /// Timeout (milliseconds)
        /// </summary>
        /// <example>1000</example>
        public int Timeout { get; init; } = 1000;

        /// <summary>
        /// 车间代码
        /// Workshop code
        /// </summary>
        /// <example>WS43400001</example>
        public required string WorkshopCode { get; init; } = "WS51401002";

        /// <summary>
        /// 设备ID
        /// Device ID
        /// </summary>
        /// <example>43400002</example>
        public required string DeviceId { get; init; } = "51440002";

        /// <summary>
        /// 员工号
        /// Employee number
        /// </summary>
        /// <example>03178298</example>
        public required string EmployeeNumber { get; init; } = "00000001";

        /// <summary>
        /// 本地服务Url
        /// Local service URL
        /// </summary>
        public string LocalServiceUrl { get; init; } = string.Empty;
    }
}
