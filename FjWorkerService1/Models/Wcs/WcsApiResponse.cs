using System;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Threading.Tasks;
using FjWorkerService1.Enums;
using System.Collections.Generic;

namespace FjWorkerService1.Models.Wcs {

    public class WcsApiResponse {

        /// <summary>
        /// 请求状态，表示请求的处理结果状态（成功、失败等）
        /// Request status, indicating the result status of the request (success, failure, etc.)
        /// </summary>
        public ApiRequestStatus RequestStatus { get; set; } = ApiRequestStatus.Success;

        /// <summary>
        /// 错误消息（如果请求失败）
        /// Error message (if request failed)
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Curl组装数据，内容可直接用于Curl访问
        /// Curl assembled data, content can be directly used for Curl access
        /// </summary>
        public string CurlData { get; set; } = string.Empty;

        /// <summary>
        /// 格式化的消息内容，便于日志记录和分析的文本形式
        /// Formatted message content, text form for logging and analysis
        /// </summary>
        public string FormattedMessage { get; set; } = string.Empty;

        /// <summary>
        /// 包裹ID
        /// Parcel ID
        /// </summary>
        public long ParcelId { get; set; }

        /// 请求地址
        /// Request URL
        public string RequestUrl { get; set; } = string.Empty;

        /// 请求内容
        /// Request body
        public string? RequestBody { get; set; }

        /// 请求头
        /// Request headers
        public string? RequestHeaders { get; set; }

        /// 请求时间
        /// Request time
        public DateTime RequestTime { get; set; } = DateTime.Now;

        /// 耗时（毫秒）
        /// Duration in milliseconds
        public long DurationMs { get; set; }

        /// 响应时间
        /// Response time
        public DateTime? ResponseTime { get; set; }

        /// 响应内容
        /// Response body
        public string? ResponseBody { get; set; }

        /// 响应状态码
        /// Response status code
        public int? ResponseStatusCode { get; set; }

        /// 响应头
        /// Response headers
        public string? ResponseHeaders { get; set; }

        /// 格式化的Curl内容
        /// Formatted CURL content
        public string? FormattedCurl { get; set; }
    }
}
