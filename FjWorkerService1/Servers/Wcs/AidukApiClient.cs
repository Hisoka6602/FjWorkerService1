using System;
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

namespace FjWorkerService1.Servers.Wcs {

    public class AidukApiClient : IWcs {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PostProcessingCenterApiClient> _logger;
        private readonly IOptionsMonitor<AidukOptions> _aidukOptions;

        public AidukApiClient(
            HttpClient httpClient,
            ILogger<PostProcessingCenterApiClient> logger,
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

            try {
                var opt = _aidukOptions.CurrentValue;

                var baseUrl = string.IsNullOrWhiteSpace(opt.PostCtnUrl)
                    ? "https://api.aiduk.cn/v1/postctn"
                    : opt.PostCtnUrl;

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
                        ResponseBody = null,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                if (!TryExtractAidukArgs(dwsData, out var args, out var extractError)) {
                    stopwatch.Stop();
                    var msg = $"Aiduk 参数提取失败：{extractError}";

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
                        ResponseBody = null,
                        ResponseStatusCode = null,
                        ResponseHeaders = null,
                        FormattedCurl = null,
                    };
                }

                // mid：优先来自 DWS；缺失时使用配置
                args.MachineId = args.MachineId > 0 ? args.MachineId : opt.MachineId;

                // t：Unix 秒时间戳
                args.TimestampSeconds = DateTimeOffset.Now.ToUnixTimeSeconds();

                var secKey = ComputeMd5Hex32Lower(string.Concat(
                    secret,
                    args.MachineId.ToString(CultureInfo.InvariantCulture),
                    args.TimestampSeconds.ToString(CultureInfo.InvariantCulture)));

                var requestUrl = BuildAidukPostCtnUrl(baseUrl, args);

                var requestHeaders = $"Content-Type: application/json\r\nseckey: {secKey}";

                var curl = ApiRequestHelper.GenerateFormattedCurl(
                    "POST",
                    requestUrl,
                    new Dictionary<string, string>(capacity: 2) {
                        ["Content-Type"] = "application/json",
                        ["seckey"] = secKey
                    },
                    "{}");

                using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
                request.Version = System.Net.HttpVersion.Version11;
                request.VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
                request.Headers.TryAddWithoutValidation("seckey", secKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                // 超时隔离：不修改 HttpClient 全局 Timeout
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
                var (bizOk, chuteId, bizCode, bizMsg) = ParseAidukResponse(responseContent);

                if (httpOk && bizOk && !string.IsNullOrWhiteSpace(chuteId)) {
                    var msg = $"Aiduk 请求格口成功，格口: {chuteId}";
                    var mergedBody = $"{responseContent}\r\n格口:[{chuteId}]";

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
                    ? $"Aiduk 请求格口失败，业务码: {bizCode}，消息: {bizMsg}"
                    : $"Aiduk 请求格口失败，HTTP 状态码: {(int)response.StatusCode}";

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

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Exception,
                    FormattedMessage = msg,
                    ErrorMessage = msg,
                    CurlData = string.Empty,
                    ParcelId = parcelId,
                    RequestUrl = string.Empty,
                    RequestBody = "{}",
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
            catch (OperationCanceledException) {
                stopwatch.Stop();
                const string msg = "Aiduk 请求格口超时取消";

                var opt = _aidukOptions.CurrentValue;
                var baseUrl = string.IsNullOrWhiteSpace(opt.PostCtnUrl)
                    ? "https://api.aiduk.cn/v1/postctn"
                    : opt.PostCtnUrl;

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Exception,
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
                    ResponseBody = null,
                    ResponseStatusCode = null,
                    ResponseHeaders = null,
                    FormattedCurl = null,
                };
            }
            catch (Exception ex) {
                stopwatch.Stop();

                var detailedMessage = ApiRequestHelper.GetDetailedExceptionMessage(ex);

                var opt = _aidukOptions.CurrentValue;
                var baseUrl = string.IsNullOrWhiteSpace(opt.PostCtnUrl)
                    ? "https://api.aiduk.cn/v1/postctn"
                    : opt.PostCtnUrl;

                var curl = ApiRequestHelper.GenerateFormattedCurl(
                    "POST",
                    baseUrl,
                    new Dictionary<string, string>(capacity: 2) {
                        ["Content-Type"] = "application/json",
                        ["seckey"] = "<seckey>"
                    },
                    "{}");

                curl = $"# 请求发生异常，可使用以下命令重试（seckey 需替换）\n{curl}";

                return new WcsApiResponse {
                    RequestStatus = ApiRequestStatus.Exception,
                    FormattedMessage = detailedMessage,
                    ErrorMessage = detailedMessage,
                    CurlData = curl,
                    ParcelId = parcelId,
                    RequestUrl = baseUrl,
                    RequestBody = "{}",
                    RequestHeaders = "Content-Type: application/json\r\nseckey: <seckey>",
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
            // Query：br/ln/wn/hn/gw/mid/t
            var sb = new StringBuilder(baseUrl.Length + 128);
            sb.Append(baseUrl);
            sb.Append(baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?');

            Append("br", args.Barcode);
            Append("ln", args.LengthMm.ToString(CultureInfo.InvariantCulture));
            Append("wn", args.WidthMm.ToString(CultureInfo.InvariantCulture));
            Append("hn", args.HeightMm.ToString(CultureInfo.InvariantCulture));
            Append("gw", args.WeightKg.ToString("0.00", CultureInfo.InvariantCulture));
            Append("mid", args.MachineId.ToString(CultureInfo.InvariantCulture));
            Append("t", args.TimestampSeconds.ToString(CultureInfo.InvariantCulture));

            // 去掉最后一个 &
            if (sb.Length > 0 && sb[^1] == '&') sb.Length--;

            return sb.ToString();

            void Append(string key, string value) {
                sb.Append(Uri.EscapeDataString(key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(value));
                sb.Append('&');
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
    }
}
