using System;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FjWorkerService1.Helpers {

    /// <summary>
    /// 格口标识解析器
    /// </summary>
    public static class ChuteIdParser {
        private const string Prefix = "格口:[";

        /// <summary>
        /// 从文本中解析形如：格口:[0001] 的数字部分并转为 int
        /// </summary>
        /// <param name="text">示例：xxx 格口:[0001] yyy</param>
        /// <param name="value">解析结果</param>
        /// <returns>成功返回 true，否则返回 false</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryParseChuteNumber(string text, out int value) {
            value = default;

            if (string.IsNullOrEmpty(text)) {
                return false;
            }

            var span = text.AsSpan();

            // 先锁定“格口:[”前缀，避免被其它 '[' 干扰
            var prefixIndex = span.IndexOf(Prefix.AsSpan());
            if (prefixIndex < 0) {
                return false;
            }

            // prefixIndex 指向“格口:[”的起始位置，数字起点在其后
            var numberStart = prefixIndex + Prefix.Length;

            if ((uint)numberStart >= (uint)span.Length) {
                return false;
            }

            // 从数字起点开始找第一个 ']'
            var rightIndex = span.Slice(numberStart).IndexOf(']');
            if (rightIndex < 0) {
                return false;
            }

            var numberSpan = span.Slice(numberStart, rightIndex);

            // 仅允许纯数字；0001 => 1
            return int.TryParse(numberSpan, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// 解析失败时抛出格式异常（异常消息为中文）
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ParseChuteNumberOrThrow(string text) {
            if (TryParseChuteNumber(text, out var value)) {
                return value;
            }

            return 0;
        }
    }
}
