using System;
using System.Web;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using FjWorkerService1.Enums;
using System.Threading.Tasks;
using System.Net.Http.Headers;
using FjWorkerService1.Helpers;
using System.Collections.Generic;
using FjWorkerService1.Models.Dws;
using FjWorkerService1.Models.Wcs;
using FjWorkerService1.Models.Conf;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace FjWorkerService1.Servers.Wcs {

    public class AidukApiClient : IWcs {
        private static readonly Regex SensitiveHeaderRegex = new(
            @"(?im)^(\s*seckey\s*:\s*).+$",
            RegexOptions.Compiled);
        private readonly HttpClient _httpClient;
        private readonly ILogger<AidukApiClient> _logger;
        private readonly IOptionsMonitor<AidukOptions> _aidukOptions;

        public AidukApiClient(
            HttpClient httpClient,
            ILogger<AidukApiClient> logger,
            IOptionsMonitor<AidukOptions> aidukOptions) {
            _httpClient = httpClient;
            _logger = logger;
            _aidukOptions = aidukOptions;
        }

        public Task<WcsApiResponse> ScanParcelAsync(long parcelId, string barcode, CancellationToken cancellationToken = default) {
            return Task.FromResult(new WcsApiResponse());
        }

        public async Task<WcsApiResponse> RequestChuteAsync(long parcelId, DwsData dwsData, CancellationToken cancellationToken = default) {
            var stopwatch = Stopwatch.StartNew();
            var requestTime = DateTime.Now;
            string curl = string.Empty;
            var requestUrl = string.Empty;
            const string requestBody = "{}";
            string requestHeaders = string.Empty;

            try {
                var opt = _aidukOptions.CurrentValue;

                if (!TryGetAidukBaseUrl(opt, out var baseUrl, out var baseUrlError)) {
                    stopwatch.Stop();

                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Failure,
                        FormattedMessage = baseUrlError,
                        ErrorMessage = baseUrlError,
                        CurlData = string.Empty,
                        ParcelId = parcelId,
                        RequestUrl = string.Empty,
                        RequestBody = "{}",
                        RequestHeaders = null,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = baseUrlError,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                var secret = opt.Secret?.Trim();
                if (string.IsNullOrWhiteSpace(secret)) {
                    stopwatch.Stop();
                    const string msg = "Aiduk 接口配置缺失：Aiduk:Secret 为空";

                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Failure,
                        FormattedMessage = msg,
                        ErrorMessage = msg,
                        CurlData = string.Empty,
                        ParcelId = parcelId,
                        RequestUrl = baseUrl,
                        RequestBody = "{}",
                        RequestHeaders = null,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = msg,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                if (!TryExtractAidukArgs(dwsData, out var args, out var extractError)) {
                    stopwatch.Stop();

                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Failure,
                        FormattedMessage = extractError,
                        ErrorMessage = extractError,
                        CurlData = string.Empty,
                        ParcelId = parcelId,
                        RequestUrl = baseUrl,
                        RequestBody = "{}",
                        RequestHeaders = null,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = extractError,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                // mid：优先来自 DWS；缺失时使用配置
                args.MachineId = args.MachineId > 0 ? args.MachineId : opt.MachineId;

                if (args.MachineId <= 0) {
                    stopwatch.Stop();
                    const string msg = "Aiduk 接口配置缺失：Aiduk:MachineId 无效";

                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Failure,
                        FormattedMessage = msg,
                        ErrorMessage = msg,
                        CurlData = string.Empty,
                        ParcelId = parcelId,
                        RequestUrl = baseUrl,
                        RequestBody = "{}",
                        RequestHeaders = null,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = msg,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                // t：Unix 秒时间戳
                args.TimestampSeconds = DateTimeOffset.Now.ToUnixTimeSeconds();

                var secKey = ComputeMd5Hex32Lower(string.Concat(
                    secret,
                    args.MachineId.ToString(CultureInfo.InvariantCulture),
                    args.TimestampSeconds.ToString(CultureInfo.InvariantCulture)));

                requestUrl = BuildAidukPostCtnUrl(baseUrl, args);
                _logger.LogInformation(
                    "[Api][Aiduk][RequestChute][REQ] parcelId={ParcelId} url={Url} barcode={Barcode} machineId={MachineId}",
                    parcelId,
                    requestUrl,
                    args.Barcode,
                    args.MachineId);

                requestHeaders = $"Content-Type: application/json\r\nseckey: {secKey}";

                curl = ApiRequestHelper.GenerateFormattedCurl(
                    "POST",
                    requestUrl,
                    new Dictionary<string, string>(capacity: 2) {
                        ["Content-Type"] = "application/json",
                        ["seckey"] = secKey
                    },
                    requestBody);

                using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
                request.Version = System.Net.HttpVersion.Version11;
                request.VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
                request.Headers.TryAddWithoutValidation("seckey", secKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (opt.TimeoutMs > 0) {
                    timeoutCts.CancelAfter(opt.TimeoutMs);
                }

                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                var responseContent = await response.Content
                    .ReadAsStringAsync(timeoutCts.Token)
                    .ConfigureAwait(false);

                stopwatch.Stop();

                var responseHeaders = string.Join("\r\n",
                    response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")
                        .Concat(response.Content.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")));
                responseContent = Regex.Unescape(responseContent);
                var (bizOk, chuteId, bizCode, bizMsg) = ParseAidukResponse(responseContent);
                var httpOk = response.IsSuccessStatusCode;

                if (httpOk && bizOk) {
                    var msg = $"Aiduk 请求格口成功，格口: {chuteId}，业务码: {bizCode}，消息: {bizMsg}";
                    var mergedBody = $"{responseContent}\r\n格口:[{chuteId}]";
                    LogResponseSummary("RequestChute", ApiRequestStatus.Success, parcelId, requestUrl, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, msg);
                    LogApiAccessRecord("RequestChute", ApiRequestStatus.Success, parcelId, requestUrl, requestHeaders, requestBody, mergedBody, stopwatch.ElapsedMilliseconds, chuteId ?? "-", msg);
                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Success,
                        FormattedMessage = msg,
                        ErrorMessage = null,
                        CurlData = curl,
                        ParcelId = parcelId,
                        RequestUrl = requestUrl,
                        RequestBody = "{}",
                        RequestHeaders = requestHeaders,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = mergedBody,
                        ResponseStatusCode = (int)response.StatusCode,
                        ResponseHeaders = responseHeaders,
                        FormattedCurl = curl,
                    };
                }

                var failMsg = httpOk
                    ? $"Aiduk 请求格口失败，格口: {chuteId}，业务码: {bizCode}，消息: {bizMsg}"
                    : $"Aiduk 请求格口失败，HTTP 状态码: {(int)response.StatusCode}";
                LogResponseSummary("RequestChute", ApiRequestStatus.Failure, parcelId, requestUrl, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, failMsg);
                LogApiAccessRecord("RequestChute", ApiRequestStatus.Failure, parcelId, requestUrl, requestHeaders, requestBody, responseContent, stopwatch.ElapsedMilliseconds, chuteId ?? "-", failMsg);

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Failure,
                    FormattedMessage = failMsg,
                    ErrorMessage = failMsg,
                    CurlData = curl,
                    ParcelId = parcelId,
                    RequestUrl = requestUrl,
                    RequestBody = "{}",
                    RequestHeaders = requestHeaders,
                    RequestTime = requestTime,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseTime = DateTime.Now,
                    ResponseBody = responseContent,
                    ResponseStatusCode = (int)response.StatusCode,
                    ResponseHeaders = responseHeaders,
                    FormattedCurl = curl,
                };

                static string ComputeMd5Hex32Lower(string payload) {
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    var hash = System.Security.Cryptography.MD5.HashData(bytes);
                    return Convert.ToHexString(hash).ToLowerInvariant();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                stopwatch.Stop();
                const string msg = "Aiduk 请求格口已取消";
                _logger.LogWarning("[Api][Aiduk][RequestChute] 已取消 parcelId={ParcelId}", parcelId);
                LogApiAccessRecord("RequestChute", ApiRequestStatus.Exception, parcelId, requestUrl, requestHeaders, requestBody, msg, stopwatch.ElapsedMilliseconds, "-", msg);

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Exception,
                    FormattedMessage = msg,
                    ErrorMessage = msg,
                    CurlData = curl,
                    ParcelId = parcelId,
                    RequestUrl = string.Empty,
                    RequestBody = "{}",
                    RequestHeaders = null,
                    RequestTime = requestTime,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseTime = DateTime.Now,
                    ResponseBody = msg,
                    ResponseStatusCode = null,
                    ResponseHeaders = null,
                    FormattedCurl = curl,
                };
            }
            catch (OperationCanceledException) {
                stopwatch.Stop();
                const string msg = "Aiduk 请求格口超时";
                _logger.LogError("[Api][Aiduk][RequestChute] 超时 parcelId={ParcelId}", parcelId);
                LogApiAccessRecord("RequestChute", ApiRequestStatus.Exception, parcelId, requestUrl, requestHeaders, requestBody, msg, stopwatch.ElapsedMilliseconds, "-", msg);

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Exception,
                    FormattedMessage = msg,
                    ErrorMessage = msg,
                    CurlData = curl,
                    ParcelId = parcelId,
                    RequestUrl = string.Empty,
                    RequestBody = "{}",
                    RequestHeaders = null,
                    RequestTime = requestTime,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseTime = DateTime.Now,
                    ResponseBody = msg,
                    ResponseStatusCode = null,
                    ResponseHeaders = null,
                    FormattedCurl = curl,
                };
            }
            catch (Exception ex) {
                stopwatch.Stop();
                _logger.LogError(ex, "[Api][Aiduk][RequestChute] 异常 parcelId={ParcelId}", parcelId);

                var detailedMessage = ApiRequestHelper.GetDetailedExceptionMessage(ex);
                LogApiAccessRecord("RequestChute", ApiRequestStatus.Exception, parcelId, requestUrl, requestHeaders, requestBody, ex.ToString(), stopwatch.ElapsedMilliseconds, "-", detailedMessage);

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Exception,
                    FormattedMessage = detailedMessage,
                    ErrorMessage = detailedMessage,
                    CurlData = curl,
                    ParcelId = parcelId,
                    RequestUrl = string.Empty,
                    RequestBody = "{}",
                    RequestHeaders = null,
                    RequestTime = requestTime,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseTime = DateTime.Now,
                    ResponseBody = ex.ToString(),
                    ResponseStatusCode = null,
                    ResponseHeaders = null,
                    FormattedCurl = curl,
                };
            }
        }

        public Task<WcsApiResponse> NotifyChuteLandingAsync(long parcelId, string chuteId, string barcode, CancellationToken cancellationToken = default) {
            return Task.FromResult(new WcsApiResponse());
        }

        public async Task<WcsApiResponse> UploadImageAsync(string barcode, byte[] imageData, CancellationToken cancellationToken = default) {
            var stopwatch = Stopwatch.StartNew();
            var requestTime = DateTime.Now;
            var curl = string.Empty;
            var requestUrl = string.Empty;
            const string requestBody = "<multipart/form-data>";
            string requestHeaders = string.Empty;

            try {
                if (string.IsNullOrWhiteSpace(barcode)) {
                    stopwatch.Stop();
                    const string msg = "Aiduk 上传图片失败：缺少 br(条码)";

                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Failure,
                        FormattedMessage = msg,
                        ErrorMessage = msg,
                        CurlData = string.Empty,
                        ParcelId = 0,
                        RequestUrl = string.Empty,
                        RequestBody = "<multipart/form-data>",
                        RequestHeaders = null,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = null,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                if (imageData is null || imageData.Length == 0) {
                    stopwatch.Stop();
                    const string msg = "Aiduk 上传图片失败：图片数据为空";

                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Failure,
                        FormattedMessage = msg,
                        ErrorMessage = msg,
                        CurlData = string.Empty,
                        ParcelId = 0,
                        RequestUrl = string.Empty,
                        RequestBody = "<multipart/form-data>",
                        RequestHeaders = null,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = null,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                var opt = _aidukOptions.CurrentValue;

                if (!TryGetAidukBaseUrl(opt, out var baseUrl, out var baseUrlError)) {
                    stopwatch.Stop();

                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Failure,
                        FormattedMessage = baseUrlError,
                        ErrorMessage = baseUrlError,
                        CurlData = string.Empty,
                        ParcelId = 0,
                        RequestUrl = string.Empty,
                        RequestBody = "<multipart/form-data>",
                        RequestHeaders = null,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = null,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                var secret = opt.Secret?.Trim();
                if (string.IsNullOrWhiteSpace(secret)) {
                    stopwatch.Stop();
                    const string msg = "Aiduk 接口配置缺失：Aiduk:Secret 为空";

                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Failure,
                        FormattedMessage = msg,
                        ErrorMessage = msg,
                        CurlData = string.Empty,
                        ParcelId = 0,
                        RequestUrl = baseUrl,
                        RequestBody = "<multipart/form-data>",
                        RequestHeaders = null,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = null,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                if (opt.MachineId <= 0) {
                    stopwatch.Stop();
                    const string msg = "Aiduk 接口配置缺失：Aiduk:MachineId 无效";

                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Failure,
                        FormattedMessage = msg,
                        ErrorMessage = msg,
                        CurlData = string.Empty,
                        ParcelId = 0,
                        RequestUrl = baseUrl,
                        RequestBody = "<multipart/form-data>",
                        RequestHeaders = null,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = null,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                var timestampSeconds = DateTimeOffset.Now.ToUnixTimeSeconds();

                var secKey = ComputeMd5Hex32Lower(string.Concat(
                    secret,
                    opt.MachineId.ToString(CultureInfo.InvariantCulture),
                    timestampSeconds.ToString(CultureInfo.InvariantCulture)));

                requestUrl = BuildAidukUpImgUrl(baseUrl, barcode.Trim(), opt.MachineId, timestampSeconds);
                _logger.LogInformation(
                    "[Api][Aiduk][UploadImage][REQ] url={Url} barcode={Barcode} bytes={Bytes} machineId={MachineId}",
                    requestUrl,
                    barcode.Trim(),
                    imageData.Length,
                    opt.MachineId);

                curl = ApiRequestHelper.GenerateFormattedCurl(
                    "POST",
                    requestUrl,
                    new Dictionary<string, string>(capacity: 1) {
                        ["seckey"] = secKey
                    },
                    "<multipart/form-data>");

                using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
                request.Version = System.Net.HttpVersion.Version11;
                request.VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
                request.Headers.TryAddWithoutValidation("seckey", secKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var multipart = new MultipartFormDataContent();

                var fileName = $"{barcode.Trim()}_{timestampSeconds}.jpeg";
                var fileContent = new ByteArrayContent(imageData);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

                // 字段名固定：wh（接口截图一致）
                multipart.Add(fileContent, "wh", fileName);

                request.Content = multipart;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (opt.TimeoutMs > 0) {
                    timeoutCts.CancelAfter(opt.TimeoutMs);
                }

                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                var responseContent = await response.Content
                    .ReadAsStringAsync(timeoutCts.Token)
                    .ConfigureAwait(false);

                stopwatch.Stop();

                var responseHeaders = string.Join("\r\n",
                    response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")
                        .Concat(response.Content.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")));

                var httpOk = response.IsSuccessStatusCode;
                var (bizOk, bizCode, bizMsg) = ParseAidukUpImgResponse(responseContent);

                requestHeaders = $"Content-Type: multipart/form-data\r\nseckey: {secKey}";

                if (httpOk && bizOk) {
                    var msg = $"Aiduk 上传图片成功，业务码: {bizCode}，消息: {bizMsg}";
                    LogResponseSummary("UploadImage", ApiRequestStatus.Success, 0, requestUrl, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, msg);
                    LogApiAccessRecord("UploadImage", ApiRequestStatus.Success, 0, requestUrl, requestHeaders, requestBody, responseContent, stopwatch.ElapsedMilliseconds, "-", msg);

                    return new WcsApiResponse {
                        RequestStatus = ApiRequestStatus.Success,
                        FormattedMessage = msg,
                        ErrorMessage = null,
                        CurlData = curl,
                        ParcelId = 0,
                        RequestUrl = requestUrl,
                        RequestBody = "<multipart/form-data>",
                        RequestHeaders = requestHeaders,
                        RequestTime = requestTime,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        ResponseTime = DateTime.Now,
                        ResponseBody = responseContent,
                        ResponseStatusCode = (int)response.StatusCode,
                        ResponseHeaders = responseHeaders,
                        FormattedCurl = curl,
                    };
                }

                var failMsg = httpOk
                    ? $"Aiduk 上传图片失败，业务码: {bizCode}，消息: {bizMsg}"
                    : $"Aiduk 上传图片失败，HTTP 状态码: {(int)response.StatusCode}";
                LogResponseSummary("UploadImage", ApiRequestStatus.Failure, 0, requestUrl, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, failMsg);
                LogApiAccessRecord("UploadImage", ApiRequestStatus.Failure, 0, requestUrl, requestHeaders, requestBody, responseContent, stopwatch.ElapsedMilliseconds, "-", failMsg);

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Failure,
                    FormattedMessage = failMsg,
                    ErrorMessage = failMsg,
                    CurlData = curl,
                    ParcelId = 0,
                    RequestUrl = requestUrl,
                    RequestBody = "<multipart/form-data>",
                    RequestHeaders = requestHeaders,
                    RequestTime = requestTime,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseTime = DateTime.Now,
                    ResponseBody = responseContent,
                    ResponseStatusCode = (int)response.StatusCode,
                    ResponseHeaders = responseHeaders,
                    FormattedCurl = curl,
                };

                static string ComputeMd5Hex32Lower(string payload) {
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    var hash = System.Security.Cryptography.MD5.HashData(bytes);
                    return Convert.ToHexString(hash).ToLowerInvariant();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                stopwatch.Stop();
                const string msg = "Aiduk 上传图片已取消";
                _logger.LogWarning("[Api][Aiduk][UploadImage] 已取消 barcode={Barcode}", barcode);
                LogApiAccessRecord("UploadImage", ApiRequestStatus.Exception, 0, requestUrl, requestHeaders, requestBody, msg, stopwatch.ElapsedMilliseconds, "-", msg);

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Exception,
                    FormattedMessage = msg,
                    ErrorMessage = msg,
                    CurlData = curl,
                    ParcelId = 0,
                    RequestUrl = string.Empty,
                    RequestBody = "<multipart/form-data>",
                    RequestHeaders = null,
                    RequestTime = requestTime,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseTime = DateTime.Now,
                    ResponseBody = null,
                    ResponseStatusCode = null,
                    ResponseHeaders = null,
                    FormattedCurl = curl,
                };
            }
            catch (OperationCanceledException) {
                stopwatch.Stop();
                const string msg = "Aiduk 上传图片超时";
                _logger.LogError("[Api][Aiduk][UploadImage] 超时 barcode={Barcode}", barcode);
                LogApiAccessRecord("UploadImage", ApiRequestStatus.Exception, 0, requestUrl, requestHeaders, requestBody, msg, stopwatch.ElapsedMilliseconds, "-", msg);

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Exception,
                    FormattedMessage = msg,
                    ErrorMessage = msg,
                    CurlData = curl,
                    ParcelId = 0,
                    RequestUrl = string.Empty,
                    RequestBody = "<multipart/form-data>",
                    RequestHeaders = null,
                    RequestTime = requestTime,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseTime = DateTime.Now,
                    ResponseBody = null,
                    ResponseStatusCode = null,
                    ResponseHeaders = null,
                    FormattedCurl = curl,
                };
            }
            catch (Exception ex) {
                stopwatch.Stop();
                _logger.LogError(ex, "[Api][Aiduk][UploadImage] 异常 barcode={Barcode}", barcode);

                var detailedMessage = ApiRequestHelper.GetDetailedExceptionMessage(ex);
                LogApiAccessRecord("UploadImage", ApiRequestStatus.Exception, 0, requestUrl, requestHeaders, requestBody, ex.ToString(), stopwatch.ElapsedMilliseconds, "-", detailedMessage);

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Exception,
                    FormattedMessage = detailedMessage,
                    ErrorMessage = detailedMessage,
                    CurlData = curl,
                    ParcelId = 0,
                    RequestUrl = string.Empty,
                    RequestBody = "<multipart/form-data>",
                    RequestHeaders = null,
                    RequestTime = requestTime,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseTime = DateTime.Now,
                    ResponseBody = ex.ToString(),
                    ResponseStatusCode = null,
                    ResponseHeaders = null,
                    FormattedCurl = curl,
                };
            }
        }

        private static string BuildAidukUpImgUrl(string baseUrl, string barcode, int machineId, long timestampSeconds) {
            var apiBase = baseUrl.TrimEnd('/') + "/";
            var endpoint = new Uri(new Uri(apiBase, UriKind.Absolute), "upimg");

            var builder = new UriBuilder(endpoint) {
                Query = $"br={Uri.EscapeDataString(barcode)}&mid={machineId.ToString(CultureInfo.InvariantCulture)}&t={timestampSeconds.ToString(CultureInfo.InvariantCulture)}"
            };

            return builder.Uri.ToString();
        }

        private static (bool BizOk, int Code, string Msg) ParseAidukUpImgResponse(string responseBody) {
            if (string.IsNullOrWhiteSpace(responseBody)) {
                return (false, -1, "响应体为空");
            }

            try {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                var code = 0;
                if (root.TryGetProperty("code", out var codeEl)) {
                    if (codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out var c1)) {
                        code = c1;
                    }
                    else if (codeEl.ValueKind == JsonValueKind.String
                             && int.TryParse(codeEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c2)) {
                        code = c2;
                    }
                }

                var msg = root.TryGetProperty("msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                    ? msgEl.GetString() ?? string.Empty
                    : string.Empty;

                // 截图返回：code=1000
                var ok = code == 1000;

                if (string.IsNullOrWhiteSpace(msg)) {
                    msg = ok ? "上传成功" : "上传失败";
                }

                return (ok, code, msg);
            }
            catch {
                return (false, -2, "响应体不是合法 JSON");
            }
        }

        private sealed record class AidukArgs {
            public required string Barcode { get; init; }
            public required int LengthMm { get; init; }
            public required int WidthMm { get; init; }
            public required int HeightMm { get; init; }
            public required decimal WeightKg { get; init; }
            public int MachineId { get; set; }
            public long TimestampSeconds { get; set; }
        }

        /// <summary>
        /// 从 DwsData 提取 Aiduk 所需字段
        /// </summary>
        private static bool TryExtractAidukArgs(DwsData dwsData, out AidukArgs args, out string error) {
            args = default!;
            error = string.Empty;

            var barcode = dwsData?.Barcode;
            if (string.IsNullOrWhiteSpace(barcode)) {
                error = "缺少 br(条码)";
                return false;
            }

            // 反射兜底：避免对 DwsData 字段命名产生硬依赖
            if (!TryGetInt(dwsData, new[] { "Length", "LengthMm", "Ln", "ln" }, out var ln)) { error = "缺少 ln(长度)"; return false; }
            if (!TryGetInt(dwsData, new[] { "Width", "WidthMm", "Wn", "wn" }, out var wn)) { error = "缺少 wn(宽度)"; return false; }
            if (!TryGetInt(dwsData, new[] { "Height", "HeightMm", "Hn", "hn" }, out var hn)) { error = "缺少 hn(高度)"; return false; }
            if (!TryGetDecimal(dwsData, new[] { "Weight", "WeightKg", "GrossWeight", "Gw", "gw" }, out var gw)) { error = "缺少 gw(重量)"; return false; }

            // mid 可选
            _ = TryGetInt(dwsData, new[] { "MachineId", "Mid", "mid" }, out var mid);

            args = new AidukArgs {
                Barcode = barcode.Trim(),
                LengthMm = ln,
                WidthMm = wn,
                HeightMm = hn,
                WeightKg = gw,
                MachineId = mid
            };

            return true;
        }

        private static string BuildAidukPostCtnUrl(string baseUrl, AidukArgs args) {
            var apiBase = baseUrl.TrimEnd('/') + "/";
            var endpoint = new Uri(new Uri(apiBase, UriKind.Absolute), "postctn");

            var builder = new UriBuilder(endpoint);

            var query = new StringBuilder(128);

            Append("br", args.Barcode);
            Append("ln", args.LengthMm.ToString(CultureInfo.InvariantCulture));
            Append("wn", args.WidthMm.ToString(CultureInfo.InvariantCulture));
            Append("hn", args.HeightMm.ToString(CultureInfo.InvariantCulture));
            Append("gw", args.WeightKg.ToString("0.00", CultureInfo.InvariantCulture));
            Append("mid", args.MachineId.ToString(CultureInfo.InvariantCulture));
            Append("t", args.TimestampSeconds.ToString(CultureInfo.InvariantCulture));

            if (query.Length > 0 && query[^1] == '&') query.Length--;
            builder.Query = query.ToString();

            return builder.Uri.ToString();

            void Append(string key, string value) {
                query.Append(Uri.EscapeDataString(key));
                query.Append('=');
                query.Append(Uri.EscapeDataString(value));
                query.Append('&');
            }
        }

        private static (bool BizOk, string? ChuteId, int BizCode, string? BizMsg) ParseAidukResponse(string json) {
            if (string.IsNullOrWhiteSpace(json)) return (false, null, -1, "响应为空");

            try {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                    ? codeEl.GetInt32()
                    : -1;

                var msg = root.TryGetProperty("msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                    ? msgEl.GetString()
                    : null;

                string? gk = null;
                if (root.TryGetProperty("gk", out var gkEl)) {
                    gk = gkEl.ValueKind switch {
                        JsonValueKind.Number => gkEl.GetInt32().ToString(CultureInfo.InvariantCulture),
                        JsonValueKind.String => gkEl.GetString(),
                        _ => null
                    };
                }

                var ok = code == 1000 && !string.IsNullOrWhiteSpace(gk);
                return (ok, gk, code, msg);
            }
            catch {
                return (false, null, -2, "响应解析失败");
            }
        }

        private static bool TryGetInt(object obj, string[] names, out int value) {
            foreach (var name in names) {
                var val = TryGetMemberValue(obj, name);
                if (val is null) continue;

                if (val is int i) { value = i; return true; }
                if (val is long l && l is >= int.MinValue and <= int.MaxValue) { value = (int)l; return true; }
                if (val is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) {
                    value = parsed; return true;
                }

                try {
                    value = Convert.ToInt32(val, CultureInfo.InvariantCulture);
                    return true;
                }
                catch {
                    // 忽略并继续
                }
            }

            value = default;
            return false;
        }

        private static bool TryGetDecimal(object obj, string[] names, out decimal value) {
            foreach (var name in names) {
                var val = TryGetMemberValue(obj, name);
                if (val is null) continue;

                if (val is decimal d) { value = d; return true; }
                if (val is double db) { value = (decimal)db; return true; }
                if (val is float f) { value = (decimal)f; return true; }
                if (val is int i) { value = i; return true; }
                if (val is long l) { value = l; return true; }

                if (val is string s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) {
                    value = parsed; return true;
                }

                try {
                    value = Convert.ToDecimal(val, CultureInfo.InvariantCulture);
                    return true;
                }
                catch {
                    // 忽略并继续
                }
            }

            value = default;
            return false;
        }

        private static object? TryGetMemberValue(object obj, string memberName) {
            var type = obj.GetType();

            // 属性
            var prop = type.GetProperty(memberName);
            if (prop is not null) return prop.GetValue(obj);

            // 字段
            var field = type.GetField(memberName);
            if (field is not null) return field.GetValue(obj);

            // 忽略大小写兜底
            var p2 = type.GetProperties().FirstOrDefault(p => string.Equals(p.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (p2 is not null) return p2.GetValue(obj);

            var f2 = type.GetFields().FirstOrDefault(f => string.Equals(f.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (f2 is not null) return f2.GetValue(obj);

            return null;
        }

        private static bool TryGetAidukBaseUrl(AidukOptions opt, out string baseUrl, out string errorMessage) {
            baseUrl = string.Empty;
            errorMessage = string.Empty;

            var raw = opt.BaseUrl?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) {
                errorMessage = "Aiduk 接口配置缺失：Aiduk:BaseUrl 为空";
                return false;
            }

            raw = raw.TrimEnd('/');

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) {
                errorMessage = $"Aiduk 接口配置非法：Aiduk:BaseUrl={raw}";
                return false;
            }

            // 约束：必须定位到 /v1（避免配置到 /v1/postctn 造成重复拼接）
            if (!uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) {
                errorMessage = $"Aiduk 接口配置非法：Aiduk:BaseUrl 必须配置到 /v1，例如 https://api.aiduk.cn/v1，当前值={raw}";
                return false;
            }

            baseUrl = raw;
            return true;
        }

        private void LogResponseSummary(
            string operation,
            ApiRequestStatus status,
            long parcelId,
            string requestUrl,
            int statusCode,
            long durationMs,
            string message) {
            var normalizedMessage = Truncate(message, 300);
            if (status == ApiRequestStatus.Success) {
                _logger.LogInformation(
                    "[Api][Aiduk][{Operation}][RESP] status={Status} parcelId={ParcelId} http={HttpStatusCode} durationMs={DurationMs} url={Url} message={Message}",
                    operation, status, parcelId, statusCode, durationMs, requestUrl, normalizedMessage);
                return;
            }

            _logger.LogWarning(
                "[Api][Aiduk][{Operation}][RESP] status={Status} parcelId={ParcelId} http={HttpStatusCode} durationMs={DurationMs} url={Url} message={Message}",
                operation, status, parcelId, statusCode, durationMs, requestUrl, normalizedMessage);
        }

        private void LogApiAccessRecord(
            string operation,
            ApiRequestStatus status,
            long parcelId,
            string requestUrl,
            string requestHeaders,
            string requestBody,
            string responseBody,
            long durationMs,
            string parsedChuteId,
            string message) {
            var sanitizedHeaders = SanitizeRequestHeaders(requestHeaders);
            var normalizedHeaders = Truncate(sanitizedHeaders, 1000);
            var normalizedRequestBody = Truncate(requestBody, 4000);
            var normalizedResponseBody = Truncate(responseBody, 4000);
            var normalizedMessage = Truncate(message, 300);

            if (status == ApiRequestStatus.Success) {
                _logger.LogInformation(
                    "[Api][爱度科][{操作}][访问记录] 状态={状态} 包裹Id={包裹Id} 地址={地址} 请求头={请求头} 请求体={请求体} 响应体={响应体} 耗时毫秒={耗时毫秒} 解析格口Id={解析格口Id} 提示={提示}",
                    operation, status, parcelId, requestUrl, normalizedHeaders, normalizedRequestBody, normalizedResponseBody, durationMs, parsedChuteId, normalizedMessage);
                return;
            }

            if (status == ApiRequestStatus.Exception) {
                _logger.LogError(
                    "[Api][爱度科][{操作}][访问记录] 状态={状态} 包裹Id={包裹Id} 地址={地址} 请求头={请求头} 请求体={请求体} 响应体={响应体} 耗时毫秒={耗时毫秒} 解析格口Id={解析格口Id} 提示={提示}",
                    operation, status, parcelId, requestUrl, normalizedHeaders, normalizedRequestBody, normalizedResponseBody, durationMs, parsedChuteId, normalizedMessage);
                return;
            }

            _logger.LogWarning(
                "[Api][爱度科][{操作}][访问记录] 状态={状态} 包裹Id={包裹Id} 地址={地址} 请求头={请求头} 请求体={请求体} 响应体={响应体} 耗时毫秒={耗时毫秒} 解析格口Id={解析格口Id} 提示={提示}",
                operation, status, parcelId, requestUrl, normalizedHeaders, normalizedRequestBody, normalizedResponseBody, durationMs, parsedChuteId, normalizedMessage);
        }

        private static string SanitizeRequestHeaders(string headers) {
            if (string.IsNullOrWhiteSpace(headers)) {
                return string.Empty;
            }

            return SensitiveHeaderRegex.Replace(headers, "$1***");
        }

        private static string Truncate(string text, int maxLength) {
            if (string.IsNullOrWhiteSpace(text)) {
                return string.Empty;
            }

            var normalized = text.Replace("\r", " ").Replace("\n", " ");
            return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
        }
    }
}
