using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FjWorkerService1.Helpers {

    public static class ApiRequestHelper {

        /// <summary>
        /// 异常嵌套追踪的最大深度
        /// Maximum depth for exception nesting tracking
        /// </summary>
        private const int MaxExceptionDepth = 10;

        /// <summary>
        /// 获取详细的异常信息，包括异常类型和所有内部异常
        /// Get detailed exception message including exception types and all inner exceptions
        /// </summary>
        /// <param name="exception">
        /// 异常对象；如果为 null，则返回空字符串。
        /// Exception object; if null, an empty string is returned.
        /// </param>
        /// <returns>
        /// 详细的异常信息，格式：[异常类型] 消息 --> [内部异常类型] 内部消息
        /// Detailed exception message in format: [ExceptionType] Message --> [InnerExceptionType] Inner Message
        /// </returns>
        public static string GetDetailedExceptionMessage(Exception? exception) {
            if (exception == null) {
                return string.Empty;
            }

            var messages = new StringBuilder();
            var currentException = exception;
            var depth = 0;

            while (currentException != null) {
                if (depth > 0) {
                    messages.Append(" --> ");
                }

                // 包含异常类型，使错误信息更清晰明确
                // Include exception type to make error message clearer and more specific
                messages.Append('[');
                messages.Append(currentException.GetType().Name);
                messages.Append("] ");
                messages.Append(currentException.Message);

                currentException = currentException.InnerException;
                depth++;

                // 防止无限循环，最多追踪指定层数
                // Prevent infinite loop, max specified levels
                if (depth >= MaxExceptionDepth) {
                    break;
                }
            }

            return messages.ToString();
        }

        /// <summary>
        /// 生成格式化的Curl命令（Windows CMD 格式）
        /// Generate formatted Curl command (Windows CMD format)
        /// </summary>
        /// <param name="method">HTTP方法</param>
        /// <param name="url">请求URL</param>
        /// <param name="headers">请求头字典</param>
        /// <param name="body">请求体</param>
        /// <returns>格式化的Curl命令字符串</returns>
        public static string GenerateFormattedCurl(
            string method,
            string url,
            Dictionary<string, string>? headers = null,
            string? body = null) {
            return GenerateWindowsCmdCurl(method, url, headers, body);
        }

        /// <summary>
        /// 生成 Windows CMD 格式的 Curl 命令（单行、无续行符）
        /// Generate Windows CMD format Curl command (single line, no continuation)
        /// </summary>
        /// <param name="method">HTTP方法 / HTTP method</param>
        /// <param name="url">请求URL / Request URL</param>
        /// <param name="headers">请求头字典 / Request headers dictionary</param>
        /// <param name="body">请求体 / Request body</param>
        /// <returns>Windows CMD 格式的 Curl 命令 / Windows CMD format Curl command</returns>
        /// <remarks>
        /// 遵循 Windows CMD 转义规则：
        /// - 所有 < 变成 ^<，所有 > 变成 ^>
        /// - 所有 | 变成 ^|（尤其是 || 必须写成 ^|^|）
        /// - 所有 & 变成 ^&
        /// - XML 属性中的双引号必须写成 ""（即 xmlns=""...""）
        /// - 命令前添加 chcp 65001>nul & 确保中文不乱码
        /// </remarks>
        private static string GenerateWindowsCmdCurl(
            string method,
            string url,
            Dictionary<string, string>? headers = null,
            string? body = null) {
            var curlBuilder = new StringBuilder();

            // 添加 chcp 65001>nul & 确保中文不乱码
            curlBuilder.Append("chcp 65001>nul & ");

            // 构建 curl 命令
            curlBuilder.Append("curl -X ");
            curlBuilder.Append(method.ToUpper());
            curlBuilder.Append(" \"");
            curlBuilder.Append(url);
            curlBuilder.Append('"');

            // 添加请求头
            if (headers != null) {
                foreach (var header in headers) {
                    curlBuilder.Append(" -H \"");
                    curlBuilder.Append(header.Key);

                    if (string.IsNullOrEmpty(header.Value)) {
                        curlBuilder.Append(':'); // 中文注释：空 SOAPAction 按 WSDL 输出为 SOAPAction:
                    }
                    else {
                        curlBuilder.Append(": ");
                        curlBuilder.Append(EscapeForWindowsCmd(header.Value));
                    }

                    curlBuilder.Append('"');
                }
            }

            // 添加请求体（需要 CMD 转义）
            if (!string.IsNullOrEmpty(body)) {
                curlBuilder.Append(" --data-raw \"");
                curlBuilder.Append(EscapeForWindowsCmd(body));
                curlBuilder.Append('"');
            }

            return curlBuilder.ToString();
        }

        /// <summary>
        /// 按 Windows CMD 规则转义字符串
        /// Escape string for Windows CMD
        /// </summary>
        /// <param name="input">输入字符串 / Input string</param>
        /// <returns>转义后的字符串 / Escaped string</returns>
        /// <remarks>
        /// CMD 转义规则：
        /// - < → ^<
        /// - > → ^>
        /// - | → ^|
        /// - & → ^&
        /// - 双引号 → "" (在双引号字符串内部，双引号本身需要双写)
        /// - 换行符和回车符被替换为空格（确保单行命令）
        /// </remarks>
        private static string EscapeForWindowsCmd(string input) {
            var sb = new StringBuilder(input.Length + (input.Length / 10));

            foreach (var ch in input) {
                switch (ch) {
                    case '"':
                        // 中文注释：CMD 双引号参数内部需要用双写双引号表示字面量双引号
                        sb.Append("\"\"");
                        break;

                    case '%':
                        // 中文注释：CMD 会进行环境变量展开，百分号需要双写避免被展开
                        sb.Append("%%");
                        break;

                    case '^':
                        // 中文注释：CMD 转义符本身需要转义，避免破坏后续字符
                        sb.Append("^^");
                        break;

                    case '\r':
                    case '\n':
                        // 中文注释：保持单行命令
                        sb.Append(' ');
                        break;

                    default:
                        sb.Append(ch);
                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 从HttpRequestMessage生成格式化的Curl命令
        /// Generate formatted Curl command from HttpRequestMessage
        /// </summary>
        public static async Task<string> GenerateFormattedCurlFromRequestAsync(HttpRequestMessage request) {
            var headers = new Dictionary<string, string>();

            // 添加请求头
            foreach (var header in request.Headers) {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            // 添加Content头
            if (request.Content?.Headers != null) {
                foreach (var header in request.Content.Headers) {
                    headers[header.Key] = string.Join(", ", header.Value);
                }
            }

            // 读取请求体
            string? body = null;
            if (request.Content != null) {
                body = await request.Content.ReadAsStringAsync();
            }

            var url = request.RequestUri?.ToString() ?? "";
            var method = request.Method.Method;

            return GenerateFormattedCurl(method, url, headers, body);
        }

        /// <summary>
        /// 需要脱敏的敏感Header名称列表（不区分大小写）
        /// List of sensitive header names that need masking (case-insensitive)
        /// </summary>
        private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
        "X-API-Key",
        "Authorization",
        "X-Auth-Token",
        "Cookie",
        "Set-Cookie",
        "X-Access-Token",
        "X-Refresh-Token",
        "Proxy-Authorization",
        "WWW-Authenticate"
    };

        /// <summary>
        /// 脱敏敏感信息：将敏感Header的值替换为"***"
        /// Mask sensitive information: Replace sensitive header values with "***"
        /// </summary>
        /// <param name="headers">原始Header字典 / Original headers dictionary</param>
        /// <returns>脱敏后的Header字典 / Masked headers dictionary</returns>
        private static Dictionary<string, string> MaskSensitiveHeaders(Dictionary<string, string> headers) {
            var maskedHeaders = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in headers) {
                if (SensitiveHeaders.Contains(kvp.Key)) {
                    // 脱敏：只显示前4个字符，其余替换为***
                    // Mask: Show only first 4 characters, replace rest with ***
                    var value = kvp.Value;
                    if (value.Length <= 4) {
                        maskedHeaders[kvp.Key] = "***";
                    }
                    else {
                        maskedHeaders[kvp.Key] = value.Substring(0, 4) + "***";
                    }
                }
                else {
                    maskedHeaders[kvp.Key] = kvp.Value;
                }
            }

            return maskedHeaders;
        }

        /// <summary>
        /// 格式化请求头为JSON字符串（自动脱敏敏感信息）
        /// Format request headers as JSON string (automatically masks sensitive information)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string FormatHeaders(Dictionary<string, string> headers) {
            var maskedHeaders = MaskSensitiveHeaders(headers);
            return JsonSerializer.Serialize(maskedHeaders, new JsonSerializerOptions {
                WriteIndented = false
            });
        }

        /// <summary>
        /// 从HttpRequestMessage获取格式化的请求头
        /// Get formatted request headers from HttpRequestMessage
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string GetFormattedHeadersFromRequest(HttpRequestMessage request) {
            var headers = new Dictionary<string, string>();

            foreach (var header in request.Headers) {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            if (request.Content?.Headers != null) {
                foreach (var header in request.Content.Headers) {
                    headers[header.Key] = string.Join(", ", header.Value);
                }
            }

            return FormatHeaders(headers);
        }

        /// <summary>
        /// 从HttpResponseMessage获取格式化的响应头
        /// Get formatted response headers from HttpResponseMessage
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string GetFormattedHeadersFromResponse(HttpResponseMessage response) {
            var headers = new Dictionary<string, string>();

            foreach (var header in response.Headers) {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            if (response.Content?.Headers != null) {
                foreach (var header in response.Content.Headers) {
                    headers[header.Key] = string.Join(", ", header.Value);
                }
            }

            return FormatHeaders(headers);
        }
    }
}
