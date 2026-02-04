using System.Text.Json;
using System.Globalization;

internal class Program {

    private static void Main(string[] args) {
        Console.WriteLine("Hello, World!");

        var json = @"{""code"":1000,""msg"":""上传成功"",""gk"":1,""data"":[]}";
        var (bizOk, chuteId, bizCode, bizMsg) = ParseAidukResponse(json);
        Console.WriteLine(bizMsg);
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
}
