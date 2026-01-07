using System.Text;
using Newtonsoft.Json;
using System.Diagnostics;
using FjWorkerService1.Enums;
using FjWorkerService1.Helpers;
using FjWorkerService1.Models.Dws;
using FjWorkerService1.Models.Wcs;
using FjWorkerService1.Models.Conf;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace FjWorkerService1.Servers.Wcs;

/// <summary>
/// 邮政处理中心API客户端实现
/// Postal Processing Center API client implementation
/// 参考: https://github.com/Hisoka6602/JayTom.Dws 分支[聚水潭(正式)] PostApi.cs
/// 使用SOAP协议进行通信，直接实现IWcsApiAdapter接口
/// Uses SOAP protocol, directly implements IWcsApiAdapter interface
/// 配置从LiteDB加载，支持热更新
/// Configuration loaded from LiteDB with hot reload support
/// </summary>
public class PostProcessingCenterApiClient : IWcs {
    private readonly HttpClient _httpClient;
    private readonly ILogger<PostProcessingCenterApiClient> _logger;
    private IConfiguration _config;
    public ConcurrentDictionary<long, PostProcessingCenterParcelInfo> ParcelInfos = new();

    // 使用线程安全的实例级序列号
    private long _sequenceNumber;

    private readonly object _sequenceLock = new();

    // 缓存配置以避免每次请求都查询数据库
    private PostProcessingCenterConfig? _cachedConfig;

    private DateTime _configCacheTime = DateTime.MinValue;
    private readonly TimeSpan _configCacheExpiry = TimeSpan.FromMinutes(5);

    // SOAP namespaces
    private const string SoapEnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";

    private const string WebServiceNamespace = "http://serverNs.webservice.pcs.jdpt.chinapost.cn/";

    // 落格回调参数常量 / Chute landing callback parameter constants
    // 参照 PostApi.UploadInBackground 的参数格式
    private const string PlaceholderZero = "0";  // 占位符字段 / Placeholder field value

    private const string SuccessStatus = "1";     // 成功落格状态 / Success landing status

    public PostProcessingCenterApiClient(
        HttpClient httpClient,
        ILogger<PostProcessingCenterApiClient> logger,
     IConfiguration config) {
        _httpClient = httpClient;
        _logger = logger;
        _config = config;
        _sequenceNumber = 1;

        //加载配置
        _cachedConfig = _config
            .GetSection("PostProcessingCenterConfig")
            .Get<PostProcessingCenterConfig>();

        if (_cachedConfig is null) {
            // 输出提示使用中文
            _logger.LogError("配置读取失败：PostProcessingCenterConfig 节点不存在或绑定失败");
            _cachedConfig = new PostProcessingCenterConfig {
                Url = "http://10.4.188.85/pcs-tc-nc-job/WyService/services/CommWY",
                WorkshopCode = "WS51401002",
                DeviceId = "51440002",
                EmployeeNumber = "00000001"
            }; // 兜底，避免影响调用方
        }
    }

    /// <summary>
    /// 获取配置，使用缓存以提高性能
    /// Get configuration with caching for performance
    /// </summary>
    private async Task<PostProcessingCenterConfig> GetConfigAsync() {
        return _cachedConfig ?? new PostProcessingCenterConfig {
            Url = string.Empty,
            WorkshopCode = string.Empty,
            DeviceId = string.Empty,
            EmployeeNumber = string.Empty,
        };
    }

    private long GetNextSequenceNumber() {
        lock (_sequenceLock) {
            return _sequenceNumber++;
        }
    }

    /// <summary>
    /// 扫描包裹到邮政处理中心
    /// Scan parcel to postal processing center (SOAP method: getYJSM)
    /// 对应PostApi.SubmitScanInfo C#方法，实际SOAP方法名: getYJSM
    /// 参数拼接格式 / Parameter format (16 fields):
    /// #HEAD::{DeviceId}::{barcode}::{EmployeeNumber}::{timestamp}::2::0::0::{WorkshopCode}::0::0::0::0::0::0::0::0::||#END
    /// </summary>
    public async Task<WcsApiResponse> ScanParcelAsync(
        long parcelId,
        string barcode,
        CancellationToken cancellationToken = default) {
        var stopwatch = Stopwatch.StartNew();
        var requestTime = DateTime.Now;
        ParcelInfos.RemoveWhen(r => DateTime.Now.Subtract(r.Value.RequestChuteTime).TotalMinutes > 10);
        try {
            // Skip NoRead barcodes
            if (barcode.Contains("NoRead", StringComparison.OrdinalIgnoreCase)) {
                stopwatch.Stop();
                const string notApplicableUrl = "SKIPPED://noread-barcode";
                var skipMessage = "NoRead barcode skipped";

                // 生成示例curl命令，展示如果处理该条码时的请求格式
                // Generate example curl command showing what the request would look like if processed
                var exampleBody = System.Text.Json.JsonSerializer.Serialize(new { barcode, operation = "scan", timestamp = DateTime.Now });
                var skipCurl = ApiRequestHelper.GenerateFormattedCurl(
                    "POST",
                    notApplicableUrl,
                    new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                    exampleBody);
                skipCurl = $"# NoRead barcode skipped - Example request format:\n{skipCurl}";

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Success,
                    FormattedMessage = skipMessage,
                    ResponseBody = skipMessage,
                    ParcelId = parcelId,
                    RequestUrl = notApplicableUrl,
                    RequestBody = null,
                    RequestHeaders = null,
                    RequestTime = requestTime,
                    ResponseTime = DateTime.Now,
                    ResponseStatusCode = 200,
                    ResponseHeaders = null,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    FormattedCurl = skipCurl,
                };
            }

            // 加载配置
            var config = await GetConfigAsync().ConfigureAwait(false);

            _logger.LogDebug("扫描包裹到邮政处理中心，条码: {Barcode}", barcode);

            // 构造SOAP请求 - 参照 PostApi.SubmitScanInfo
            var arg0 = new StringBuilder()
                .Append("#HEAD::")
                .Append(config.DeviceId).Append("::")
                .Append(barcode).Append("::")
                .Append(config.EmployeeNumber).Append("::")
                .Append(DateTime.Now.ToString("yyyyMMddHHmmss")).Append("::")
                .Append("2::0::0::")
                .Append(config.WorkshopCode).Append("::")
                .Append("0::0::0::0::0::0::0::0::")
                .Append("||#END")
                .ToString();

            var soapRequest = BuildSoapEnvelope("getYJSM", arg0);

            // 生成请求头信息用于日志记录
            var requestHeaders = "Content-Type: text/xml; charset=utf-8";

            // 生成curl命令
            var curlCommand = ApiRequestHelper.GenerateFormattedCurl(
                "POST",
                config.Url,
                new Dictionary<string, string> {
                    ["Content-Type"] = "text/xml; charset=utf-8",
                    ["SOAPAction"] = string.Empty
                },
                soapRequest);

            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");

            var response = await _httpClient.PostAsync(config.Url, content, cancellationToken).ConfigureAwait(false);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            responseContent = Regex.Unescape(responseContent);

            stopwatch.Stop();

            // 获取响应头信息
            var responseHeaders = string.Join("\r\n", response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));

            if (response.IsSuccessStatusCode) {
                _logger.LogInformation("扫描包裹成功（邮政处理中心），条码: {Barcode}", barcode);

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Success,
                    FormattedMessage = "Parcel scanned successfully at postal processing center",
                    ResponseBody = responseContent,
                    ParcelId = parcelId,
                    RequestUrl = config.Url,
                    RequestBody = soapRequest,
                    RequestHeaders = requestHeaders,
                    RequestTime = requestTime,
                    ResponseTime = DateTime.Now,
                    ResponseStatusCode = (int)response.StatusCode,
                    ResponseHeaders = responseHeaders,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    FormattedCurl = curlCommand,
                };
            }
            else {
                _logger.LogWarning("扫描包裹失败（邮政处理中心），条码: {Barcode}, 状态码: {StatusCode}",
                    barcode, response.StatusCode);

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Failure,
                    FormattedMessage = $"Scan Error: {response.StatusCode}",
                    ResponseBody = responseContent,
                    ErrorMessage = $"Scan Error: {response.StatusCode}",
                    ParcelId = parcelId,
                    RequestUrl = config.Url,
                    RequestBody = soapRequest,
                    RequestHeaders = requestHeaders,
                    RequestTime = requestTime,
                    ResponseTime = DateTime.Now,
                    ResponseStatusCode = (int)response.StatusCode,
                    ResponseHeaders = responseHeaders,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    FormattedCurl = curlCommand,
                };
            }
        }
        catch (Exception ex) {
            stopwatch.Stop();
            _logger.LogError(ex, "扫描包裹异常（邮政处理中心），条码: {Barcode}", barcode);

            // 获取详细的异常信息，包括所有内部异常
            // Get detailed exception message including all inner exceptions
            var detailedMessage = ApiRequestHelper.GetDetailedExceptionMessage(ex);

            // 加载配置以获取URL（如果可能）
            var config = await GetConfigAsync().ConfigureAwait(false);

            // 构造SOAP请求用于生成curl（即使异常也需要生成curl）
            var arg0 = new StringBuilder()
                .Append("#HEAD::")
                .Append(config.DeviceId).Append("::")
                .Append(barcode).Append("::")
                .Append(config.EmployeeNumber).Append("::")
                .Append(DateTime.Now.ToString("yyyyMMddHHmmss")).Append("::")
                .Append("2::0::0::")
                .Append(config.WorkshopCode).Append("::")
                .Append("0::0::0::0::0::0::0::0::")
                .Append("||#END")
                .ToString();
            var soapRequest = BuildSoapEnvelope("getYJSM", arg0);

            // 生成请求头信息
            var requestHeaders = "Content-Type: text/xml; charset=utf-8";

            // 生成curl命令（异常情况下也必须生成）
            var curlCommand = ApiRequestHelper.GenerateFormattedCurl(
                "POST",
                config.Url,
                new Dictionary<string, string> {
                    ["Content-Type"] = "text/xml; charset=utf-8",
                    ["SOAPAction"] = string.Empty
                },
                soapRequest);
            curlCommand = $"# Exception occurred during request - Curl command for retry:\n{curlCommand}";

            return new WcsApiResponse {
                RequestStatus = ApiRequestStatus.Exception,
                FormattedMessage = detailedMessage,
                ResponseBody = ex.ToString(),
                ErrorMessage = detailedMessage,
                ParcelId = parcelId,
                RequestUrl = config.Url,
                RequestBody = soapRequest,
                RequestHeaders = requestHeaders,
                RequestTime = requestTime,
                ResponseTime = DateTime.Now,
                ResponseStatusCode = null,
                ResponseHeaders = null,
                DurationMs = stopwatch.ElapsedMilliseconds,
                FormattedCurl = curlCommand,
            };
        }
    }

    /// <summary>
    /// 请求格口号（查询包裹信息并返回格口）
    /// Request chute number (SOAP method: getGKCX)
    /// 对应PostApi.UploadData C#方法，实际SOAP方法名: getGKCX
    /// 参数拼接格式 / Parameter format (7 fields, fields 5-7 are single space):
    /// #HEAD::{sequenceId}::{barcode}::{DeviceId}::{WorkshopCode}::{空格}::{空格}::{空格}::||#END
    /// 其中字段5-7固定为单个空格字符（" "），作为占位字段，不能省略
    /// sequenceId格式: {yyyyMM}{WorkshopCode}FJ{序号9位}
    /// </summary>
    public async Task<WcsApiResponse> RequestChuteAsync(
        long parcelId,
        DwsData dwsData,
        CancellationToken cancellationToken = default) {
        var stopwatch = Stopwatch.StartNew();
        var requestTime = DateTime.Now;

        try {
            _logger.LogInformation("请求格口（邮政处理中心），包裹ID: {ParcelId}, 条码: {Barcode}\n",
                parcelId, dwsData.Barcode);
            _logger.LogDebug("请求格口（邮政处理中心），包裹ID: {ParcelId}, 条码: {Barcode}",
                parcelId, dwsData.Barcode);

            // 先提交扫描信息
            await ScanParcelAsync(parcelId, dwsData.Barcode, cancellationToken).ConfigureAwait(false);

            // 加载配置
            var config = await GetConfigAsync().ConfigureAwait(false);

            var seqNum = GetNextSequenceNumber();
            var yearMonth = DateTime.Now.ToString("yyyyMM");
            var sequenceId = $"{yearMonth}{config.WorkshopCode}FJ{seqNum.ToString().PadLeft(9, '0')}";

            // 构造格口查询SOAP请求 - 参照 PostApi.UploadData
            var arg0 = new StringBuilder()
                .Append("#HEAD::")
                .Append(sequenceId).Append("::")
                .Append(dwsData.Barcode).Append("::")
                .Append(config.DeviceId).Append("::")
                .Append(config.WorkshopCode).Append("::")
                .Append(" ").Append("::") // 第5字段：空格
                .Append(" ").Append("::") // 第6字段：空格
                .Append(" ").Append("::") // 第7字段：空格
                .Append("||#END")
                .ToString();

            var soapRequest = BuildSoapEnvelope("getGKCX", arg0);

            // 生成请求头信息用于日志记录
            var requestHeaders = "Content-Type: text/xml; charset=utf-8";

            // 生成curl命令
            var curlCommand = ApiRequestHelper.GenerateFormattedCurl(
                "POST",
                config.Url,
                new Dictionary<string, string> {
                    ["Content-Type"] = "text/xml; charset=utf-8",
                    ["SOAPAction"] = string.Empty
                },
                soapRequest);

            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");

            var response = await _httpClient.PostAsync(config.Url, content, cancellationToken).ConfigureAwait(false);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            responseContent = Regex.Unescape(responseContent);

            stopwatch.Stop();

            // 获取响应头信息
            var responseHeaders = string.Join("\r\n",
                response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));

            // 提取格口信息
            var chute = ExtractChuteFromResponse(responseContent);
            var isSuccess = !string.IsNullOrEmpty(chute);
            _logger.LogError(
                $"conf:{JsonConvert.SerializeObject(config)},content:{soapRequest},curlCommand:{curlCommand}");
            _logger.LogError(
                $"response.IsSuccessStatusCode:{response.IsSuccessStatusCode},chute:{chute},responseContent:{responseContent}");
            if (response.IsSuccessStatusCode && isSuccess) {
                _logger.LogInformation(
                    "请求格口成功（邮政处理中心），包裹ID: {ParcelId}, 条码: {Barcode}, 格口: {Chute}, 耗时: {Duration}ms",
                    parcelId, dwsData.Barcode, chute, stopwatch.ElapsedMilliseconds);

                //需要在这里加上格口的解析
                if (!string.IsNullOrWhiteSpace(responseContent)) {
                    var pattern = @"#HEAD::(.*?)::\|\|#END";
                    var match = Regex.Match(responseContent, pattern);
                    if (match.Success) {
                        // Extract the content and split by '::'
                        var RegexContent = match.Groups[1].Value;
                        var parts = RegexContent.Split(new[] { "::" }, StringSplitOptions.None);

                        if (parts.Length > 7 && parts[6].Length >= 8) {
                            var exit = $"格口:[{parts[6][..4]}]";
                            //暂时不判断备用格口
                            responseContent += exit;
                        }

                        //缓存信息
                        if (!ParcelInfos.ContainsKey(Convert.ToInt64(parcelId))) {
                            var parcelInfo = new PostProcessingCenterParcelInfo {
                                MailRouteCode = parts[1],
                                DirectionChuteCode = parts[3],
                                MailKind = parts[5],
                                TargetChuteId = Convert.ToInt64((parts[6][..4])),
                                PlanId = parts[2],
                                RequestChuteTime = DateTime.Now
                            };
                            ParcelInfos.TryAdd(Convert.ToInt64(parcelId), parcelInfo);
                        }
                    }
                }

                var wcsApiResponse = new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Success,
                    FormattedMessage = "Chute requested successfully",
                    ResponseBody = responseContent,
                    ParcelId = parcelId,
                    RequestUrl = config.Url,
                    RequestBody = soapRequest,
                    RequestHeaders = requestHeaders,
                    RequestTime = requestTime,
                    ResponseTime = DateTime.Now,
                    ResponseStatusCode = (int)response.StatusCode,
                    ResponseHeaders = responseHeaders,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    FormattedCurl = curlCommand,
                };
                _logger.LogInformation(JsonConvert.SerializeObject(wcsApiResponse));
                return wcsApiResponse;
            }
            else {
                _logger.LogWarning(
                    "请求格口失败（邮政处理中心），包裹ID: {ParcelId}, 条码: {Barcode}, 状态码: {StatusCode}, 耗时: {Duration}ms",
                    parcelId, dwsData.Barcode, response.StatusCode, stopwatch.ElapsedMilliseconds);

                var wcsApiResponse = new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Failure,
                    FormattedMessage = $"Chute Request Error: {response.StatusCode}",
                    ResponseBody = responseContent,
                    ErrorMessage = $"Chute Request Error: {response.StatusCode}",
                    ParcelId = parcelId,
                    RequestUrl = config.Url,
                    RequestBody = soapRequest,
                    RequestHeaders = requestHeaders,
                    RequestTime = requestTime,
                    ResponseTime = DateTime.Now,
                    ResponseStatusCode = (int)response.StatusCode,
                    ResponseHeaders = responseHeaders,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    FormattedCurl = curlCommand,
                };
                _logger.LogInformation(JsonConvert.SerializeObject(wcsApiResponse));
                return wcsApiResponse;
            }
        }
        catch (Exception ex) {
            stopwatch.Stop();
            _logger.LogError(ex, "请求格口异常（邮政处理中心），包裹ID: {ParcelId}, 耗时: {Duration}ms",
                parcelId, stopwatch.ElapsedMilliseconds);

            // 获取详细的异常信息，包括所有内部异常
            // Get detailed exception message including all inner exceptions
            var detailedMessage = ApiRequestHelper.GetDetailedExceptionMessage(ex);

            // 加载配置以获取URL（如果可能）
            var config = await GetConfigAsync().ConfigureAwait(false);

            // 构造SOAP请求用于生成curl（即使异常也需要生成curl）- 参照 PostApi.UploadData
            var seqNum = GetNextSequenceNumber();
            var yearMonth = DateTime.Now.ToString("yyyyMM");
            var sequenceId = $"{yearMonth}{config.WorkshopCode}FJ{seqNum.ToString().PadLeft(9, '0')}";

            var arg0 = new StringBuilder()
                .Append("#HEAD::")
                .Append(sequenceId).Append("::")
                .Append(dwsData.Barcode).Append("::")
                .Append(config.DeviceId).Append("::")
                .Append(config.WorkshopCode).Append("::")
                .Append(" :: :: ")
                .Append("||#END")
                .ToString();
            var soapRequest = BuildSoapEnvelope("getGKCX", arg0);

            // 生成请求头信息
            var requestHeaders = "Content-Type: text/xml; charset=utf-8";

            // 生成curl命令（异常情况下也必须生成）
            var curlCommand = ApiRequestHelper.GenerateFormattedCurl(
                "POST",
                config.Url,
                new Dictionary<string, string> {
                    ["Content-Type"] = "text/xml; charset=utf-8",
                    ["SOAPAction"] = string.Empty
                },
                soapRequest);
            curlCommand = $"# Exception occurred during request - Curl command for retry:\n{curlCommand}";

            var wcsApiResponse = new WcsApiResponse {
                RequestStatus = ApiRequestStatus.Exception,
                FormattedMessage = detailedMessage,
                ResponseBody = ex.ToString(),
                ErrorMessage = detailedMessage,
                ParcelId = parcelId,
                RequestUrl = config.Url,
                RequestBody = soapRequest,
                RequestHeaders = requestHeaders,
                RequestTime = requestTime,
                ResponseTime = DateTime.Now,
                ResponseStatusCode = null,
                ResponseHeaders = null,
                DurationMs = stopwatch.ElapsedMilliseconds,
                FormattedCurl = curlCommand,
            };
            _logger.LogInformation(JsonConvert.SerializeObject(wcsApiResponse));
            return wcsApiResponse;
        }
        finally {
            _logger.LogInformation("-----------------\n");
        }
    }

    /// <summary>
    /// 落格回调 - 通知邮政处理中心包裹已落入指定格口
    /// Chute landing callback (SOAP method: getYJLG)
    /// 对应PostApi.UploadInBackground C#方法，实际SOAP方法名: getYJLG
    /// 对应参考实现: https://github.com/Hisoka6602/JayTom.Dws 分支[聚水潭(正式)] PostApi.UploadInBackground
    /// 参数拼接格式 / Parameter format (22 fields):
    /// #HEAD::{DeviceId}::{barcode}::{0}::{0}::{EmployeeNumber}::{0}::{timestamp}::{routingDirection}::{lcgk}::{mailType}::{chuteCode}::{WorkshopCode}::{1}::{0}::{0}::{0}::{0}::{0}::{0}::{0}::{0}::{playId}::||#END
    /// </summary>
    public async Task<WcsApiResponse> NotifyChuteLandingAsync(
        long parcelId,
        string chuteId,
        string barcode,
        CancellationToken cancellationToken = default) {
        var stopwatch = Stopwatch.StartNew();
        var requestTime = DateTime.Now;
        ParcelInfos.TryGetValue(Convert.ToInt64(parcelId), out var info);
        try {
            _logger.LogInformation("落格回调（邮政处理中心），包裹ID: {ParcelId}, 格口: {ChuteId}, 条码: {Barcode}",
                parcelId, chuteId, barcode);

            // 加载配置
            var config = await GetConfigAsync().ConfigureAwait(false);

            // 构造落格回调SOAP请求 - 参照 PostApi.UploadInBackground
            // 参考格式: #HEAD::{DeviceId}::{barcode}::{0}::{0}::{EmployeeNumber}::{0}::{timestamp}::{routingDirection}::{lcgk}::{mailType}::{chuteCode}::{WorkshopCode}::{1}::{0}::{0}::{0}::{0}::{0}::{0}::{0}::{0}::{playId}::||#END
            var arg0 = new StringBuilder()
                .Append("#HEAD::")
                .Append(config.DeviceId).Append("::")
                .Append(barcode).Append("::")
                .Append(PlaceholderZero).Append("::") // 实际重量
                .Append(PlaceholderZero).Append("::") // 供包台号
                .Append(config.EmployeeNumber).Append("::") //操作员编号
                .Append(PlaceholderZero).Append("::") // 小车编号
                .Append(DateTime.Now.ToString("yyyyMMddHHmmss")).Append("::") //落格时间
                .Append(info?.MailRouteCode ?? PlaceholderZero).Append("::") // 路由代码
                .Append(info?.TargetChuteId ?? 0).Append("::") // 路向格口
                .Append(info?.MailKind).Append("::") //邮件种类
                .Append(chuteId).Append("::")
                .Append(config.WorkshopCode).Append("::")
                .Append(Convert.ToInt64(chuteId) == info?.TargetChuteId ? "1" : "2").Append("::") // Status: 1=成功落格
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(info?.PlanId ?? PlaceholderZero).Append("::") // playId
                .Append("::")
                .Append("||#END")
                .ToString();

            var soapRequest = BuildSoapEnvelope("getYJLG", arg0);

            // 生成请求头信息用于日志记录
            var requestHeaders = "Content-Type: text/xml; charset=utf-8";

            // 生成curl命令
            var curlCommand = ApiRequestHelper.GenerateFormattedCurl(
                "POST",
                config.Url,
                new Dictionary<string, string> {
                    ["Content-Type"] = "text/xml; charset=utf-8",
                    ["SOAPAction"] = string.Empty
                },
                soapRequest);

            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");

            var response = await _httpClient.PostAsync(config.Url, content, cancellationToken).ConfigureAwait(false);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            responseContent = Regex.Unescape(responseContent);

            stopwatch.Stop();

            // 获取响应头信息
            var responseHeaders = string.Join("\r\n",
                response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));

            if (response.IsSuccessStatusCode) {
                _logger.LogInformation(
                    "落格回调成功（邮政处理中心），包裹ID: {ParcelId}, 格口: {ChuteId}, 条码: {Barcode}",
                    parcelId, chuteId, barcode);

                var wcsApiResponse = new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Success,
                    FormattedMessage = "Chute landing notification sent successfully",
                    ResponseBody = responseContent,
                    ParcelId = parcelId,
                    RequestUrl = config.Url,
                    RequestBody = soapRequest,
                    RequestHeaders = requestHeaders,
                    RequestTime = requestTime,
                    ResponseTime = DateTime.Now,
                    ResponseStatusCode = (int)response.StatusCode,
                    ResponseHeaders = responseHeaders,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    FormattedCurl = curlCommand,
                };
                _logger.LogInformation(JsonConvert.SerializeObject(wcsApiResponse));
                return wcsApiResponse;
            }
            else {
                _logger.LogWarning(
                    "落格回调失败（邮政处理中心），包裹ID: {ParcelId}, 格口: {ChuteId}, 状态码: {StatusCode}",
                    parcelId, chuteId, response.StatusCode);

                var wcsApiResponse = new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Failure,
                    FormattedMessage = $"Chute landing notification error: {response.StatusCode}",
                    ResponseBody = responseContent,
                    ErrorMessage = $"Chute landing notification error: {response.StatusCode}",
                    ParcelId = parcelId,
                    RequestUrl = config.Url,
                    RequestBody = soapRequest,
                    RequestHeaders = requestHeaders,
                    RequestTime = requestTime,
                    ResponseTime = DateTime.Now,
                    ResponseStatusCode = (int)response.StatusCode,
                    ResponseHeaders = responseHeaders,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    FormattedCurl = curlCommand,
                };
                _logger.LogInformation(JsonConvert.SerializeObject(wcsApiResponse));
                return wcsApiResponse;
            }
        }
        catch (Exception ex) {
            stopwatch.Stop();
            _logger.LogError(ex, "落格回调异常（邮政处理中心），包裹ID: {ParcelId}, 格口: {ChuteId}",
                parcelId, chuteId);

            // 获取详细的异常信息，包括所有内部异常
            // Get detailed exception message including all inner exceptions
            var detailedMessage = ApiRequestHelper.GetDetailedExceptionMessage(ex);

            // 加载配置以获取URL（如果可能）
            var config = await GetConfigAsync().ConfigureAwait(false);

            // 构造SOAP请求用于生成curl（即使异常也需要生成curl）- 参照 PostApi.UploadInBackground
            var arg0 = new StringBuilder()
                .Append("#HEAD::")
                .Append(config.DeviceId).Append("::")
                .Append(barcode).Append("::")
                .Append(PlaceholderZero).Append("::") // 实际重量
                .Append(PlaceholderZero).Append("::") // 供包台号
                .Append(config.EmployeeNumber).Append("::") //操作员编号
                .Append(PlaceholderZero).Append("::") // 小车编号
                .Append(DateTime.Now.ToString("yyyyMMddHHmmss")).Append("::") //落格时间
                .Append(info?.MailRouteCode ?? PlaceholderZero).Append("::") // 路由代码
                .Append(info?.TargetChuteId ?? 0).Append("::") // 路向格口
                .Append(info?.MailKind).Append("::") //邮件种类
                .Append(chuteId).Append("::")
                .Append(config.WorkshopCode).Append("::")
                .Append(Convert.ToInt64(chuteId) == info?.TargetChuteId ? "1" : "2").Append("::") // Status: 1=成功落格
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(PlaceholderZero).Append("::")
                .Append(info?.PlanId ?? PlaceholderZero).Append("::") // playId
                .Append("::")
                .Append("||#END")
                .ToString();
            var soapRequest = BuildSoapEnvelope("getYJLG", arg0);

            // 生成请求头信息
            var requestHeaders = "Content-Type: text/xml; charset=utf-8";

            // 生成curl命令（异常情况下也必须生成）
            var curlCommand = ApiRequestHelper.GenerateFormattedCurl(
                "POST",
                config.Url,
                new Dictionary<string, string> {
                    ["Content-Type"] = "text/xml; charset=utf-8",
                    ["SOAPAction"] = string.Empty
                },
                soapRequest);
            curlCommand = $"# Exception occurred during request - Curl command for retry:\n{curlCommand}";

            var wcsApiResponse = new WcsApiResponse {
                RequestStatus = ApiRequestStatus.Exception,
                FormattedMessage = detailedMessage,
                ResponseBody = ex.ToString(),
                ErrorMessage = detailedMessage,
                ParcelId = parcelId,
                RequestUrl = config.Url,
                RequestBody = soapRequest,
                RequestHeaders = requestHeaders,
                RequestTime = requestTime,
                ResponseTime = DateTime.Now,
                ResponseStatusCode = null,
                ResponseHeaders = null,
                DurationMs = stopwatch.ElapsedMilliseconds,
                FormattedCurl = curlCommand,
            };
            _logger.LogInformation(JsonConvert.SerializeObject(wcsApiResponse));
            return wcsApiResponse;
        }
        finally {
            _logger.LogInformation("-----------------\n");
        }
    }

    /// <summary>
    /// 从SOAP响应中提取格口信息
    /// Extract chute from SOAP response
    /// Response format: #HEAD::{field0}::{field1}::{...}::{field7(chute)}::{...}::||#END
    /// </summary>
    private static string? ExtractChuteFromResponse(string responseContent) {
        if (string.IsNullOrWhiteSpace(responseContent)) {
            return null;
        }

        var pattern = @"#HEAD::(.*?)::\|\|#END";
        var match = Regex.Match(responseContent, pattern);
        if (match.Success) {
            var content = match.Groups[1].Value;
            var parts = content.Split(new string[] { "::" }, StringSplitOptions.None);

            // Chute is in field 7 (index 7), take first 4 characters
            if (parts.Length > 7 && parts[6].Length >= 4) {
                return parts[6][..4];
            }
        }

        return null;
    }

    /// <summary>
    /// 构建SOAP信封
    /// Build SOAP envelope
    /// </summary>
    private static string BuildSoapEnvelope(string methodName, string arg0Value) {
        return $@"<soapenv:Envelope xmlns:soapenv=""{SoapEnvelopeNamespace}"" xmlns:web=""{WebServiceNamespace}"">
    <soapenv:Header/>
    <soapenv:Body>
        <web:{methodName}>
            <arg0>{arg0Value}</arg0>
        </web:{methodName}>
    </soapenv:Body>
</soapenv:Envelope>";
    }

    /// <summary>
    /// 集包中心邮件信息（契约模型）
    /// </summary>
    public sealed record class PostProcessingCenterParcelInfo {
        /// <summary>
        /// 邮路代码
        /// </summary>
        public required string MailRouteCode { get; init; }

        /// <summary>
        /// 路向格口（业务定义的格口/路向编码）
        /// </summary>
        public required string DirectionChuteCode { get; init; }

        /// <summary>
        /// 邮件种类
        /// </summary>
        public required string MailKind { get; init; }

        /// <summary>
        /// 方案ID
        /// </summary>
        public required string PlanId { get; init; }

        /// <summary>
        /// 目标格口（目标格口ID/编号，按系统约定）
        /// </summary>
        public required long TargetChuteId { get; init; }
        /// <summary>
        /// 请求时间
        /// </summary>
        public required DateTime RequestChuteTime { get; init; }
    }
}
