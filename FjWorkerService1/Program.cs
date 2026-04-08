using NLog;
using FjWorkerService1;
using NLog.Extensions.Logging;
using FjWorkerService1.Servers;
using FjWorkerService1.Utilities;
using FjWorkerService1.Models.Conf;
using FjWorkerService1.Servers.Dws;
using FjWorkerService1.Servers.Wcs;
using Microsoft.Extensions.Options;
using FjWorkerService1.Servers.Sorter;
using FjWorkerService1.BackgroundServices;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using FjWorkerService1.BackgroundServices.FileSystemImage;

var nlogConfigPath = Path.Combine(AppContext.BaseDirectory, "nlog.config");
LogManager.Setup().LoadConfigurationFromFile(nlogConfigPath);
var bootstrapLogger = LogManager.GetCurrentClassLogger();

try {
    var builder = Host.CreateApplicationBuilder(args);

    // Windows 服务

#if RELEASE
    builder.Services.AddWindowsService(options => { options.ServiceName = "FjWorkerService"; });
#endif

    // 切换到 NLog
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Logging.AddNLog();

    // ======= 保持原有服务注册（在此基础上扩展） =======
    builder.Services.Configure<LogCleanupSettings>(
        builder.Configuration.GetSection("LogCleanup"));
    builder.Services.AddSingleton<SafeExecutor>();

    builder.Services
        .AddOptions<DataFusionOptions>()
        .Bind(builder.Configuration.GetSection("DataFusion"))
        .Validate(o => o.Timeout > 0, "配置无效：DataFusion:Timeout 必须大于 0")
        .ValidateOnStart();
    builder.Services
        .AddOptions<ImageMonitoringOptions>()
        .Bind(builder.Configuration.GetSection("ImageMonitoring"))
        .ValidateOnStart();
    var wcsProvider = builder.Configuration.GetValue<string>("Wcs:Provider")?.Trim();
    if (string.IsNullOrWhiteSpace(wcsProvider)) {
        wcsProvider = "PostProcessingCenterApiClient";
    }

    if (wcsProvider.Equals("PostProcessingCenterApiClient", StringComparison.OrdinalIgnoreCase)
        || wcsProvider.Equals("PostProcessingCenter", StringComparison.OrdinalIgnoreCase)) {
        builder.Services
            .AddHttpClient<IWcs, PostProcessingCenterApiClient>((sp, client) => {
                var configuration = sp.GetRequiredService<IConfiguration>();

                var url = configuration.GetValue<string>("PostProcessingCenterConfig:Url");
                if (string.IsNullOrWhiteSpace(url)) {
                    throw new InvalidOperationException("Missing config: PostProcessingCenterConfig:Url");
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)) {
                    throw new InvalidOperationException($"Invalid config: PostProcessingCenterConfig:Url = {url}");
                }

                var timeoutMs = configuration.GetValue<int?>("PostProcessingCenterConfig:Timeout") ?? 1000;
                client.BaseAddress = baseUri;
                client.Timeout = timeoutMs <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(timeoutMs);
            });
    }
    else if (wcsProvider.Equals("AidukApiClient", StringComparison.OrdinalIgnoreCase)
             || wcsProvider.Equals("Aiduk", StringComparison.OrdinalIgnoreCase)) {
        builder.Services
            .AddOptions<AidukOptions>()
            .Configure<IConfiguration>((opt, cfg) => {
                opt.BaseUrl =
                    cfg["Aiduk:PostCtnUrl"]
                    ?? cfg["AidukConfig:PostCtnUrl"]
                    ?? cfg["Aiduk:Url"]
                    ?? cfg["AidukConfig:Url"]
                    ?? "https://api.aiduk.cn/v1/postctn";

                opt.Secret =
                    cfg["Aiduk:Secret"]
                    ?? cfg["AidukConfig:Secret"]
                    ?? cfg["Aiduk:secret"]
                    ?? cfg["AidukConfig:secret"]
                    ?? string.Empty;

                opt.MachineId =
                    cfg.GetValue<int?>("Aiduk:MachineId")
                    ?? cfg.GetValue<int?>("AidukConfig:MachineId")
                    ?? 0;

                // 兼容 Timeout / TimeoutMs 两种配置名
                opt.TimeoutMs =
                    cfg.GetValue<int?>("Aiduk:Timeout")
                    ?? cfg.GetValue<int?>("Aiduk:TimeoutMs")
                    ?? cfg.GetValue<int?>("AidukConfig:Timeout")
                    ?? cfg.GetValue<int?>("AidukConfig:TimeoutMs")
                    ?? 1000;
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl), "Invalid config: Aiduk:PostCtnUrl cannot be empty")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Secret), "Invalid config: Aiduk:Secret cannot be empty")
            .Validate(o => o.TimeoutMs > 0, "Invalid config: Aiduk:Timeout must be greater than 0")
            .ValidateOnStart();

        builder.Services
            .AddHttpClient<IWcs, AidukApiClient>((sp, client) => {
                var opt = sp.GetRequiredService<IOptionsMonitor<AidukOptions>>().CurrentValue;

                var url = opt.BaseUrl;
                if (string.IsNullOrWhiteSpace(url)) {
                    throw new InvalidOperationException("Missing config: Aiduk:PostCtnUrl");
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)) {
                    throw new InvalidOperationException($"Invalid config: Aiduk:PostCtnUrl = {url}");
                }

                var secret = opt.Secret?.Trim();
                if (string.IsNullOrWhiteSpace(secret)) {
                    throw new InvalidOperationException("Missing config: Aiduk:Secret");
                }

                var timeoutMs = opt.TimeoutMs;

                client.BaseAddress = baseUri;
                client.Timeout = timeoutMs <= 0
                    ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromMilliseconds(timeoutMs);
            });
    }
    else {
        throw new InvalidOperationException(
            $"Invalid config: Wcs:Provider = {wcsProvider}. Allowed values: PostProcessingCenterApiClient, AidukApiClient");
    }

    const string dwsOptionsName = "Dws";
    const string sorterOptionsName = "Sorter";

    builder.Services
        .AddOptions<TcpConnectConfig>(dwsOptionsName)
        .Bind(builder.Configuration.GetSection("DwsTcpConnectConfig"))
        .Validate(c => !string.IsNullOrWhiteSpace(c.Ip), "配置无效：DwsTcpConnectConfig:Ip 不能为空")
        .Validate(c => c.Port is > 0 and <= 65535, "配置无效：DwsTcpConnectConfig:Port 范围必须为 1-65535")
        .ValidateOnStart();

    builder.Services
        .AddOptions<TcpConnectConfig>(sorterOptionsName)
        .Bind(builder.Configuration.GetSection("SorterTcpConnectConfig"))
        .Validate(c => !string.IsNullOrWhiteSpace(c.Ip), "配置无效：SorterTcpConnectConfig:Ip 不能为空")
        .Validate(c => c.Port is > 0 and <= 65535, "配置无效：SorterTcpConnectConfig:Port 范围必须为 1-65535")
        .ValidateOnStart();

    builder.Services.AddSingleton<IDws>(sp => {
        var config = sp.GetRequiredService<IOptionsMonitor<TcpConnectConfig>>().Get(dwsOptionsName);
        var dwsLogger = sp.GetRequiredService<ILogger<DefaultDws>>();
        return new DefaultDws(config, dwsLogger);
    });

    builder.Services.AddSingleton<ISorter>(sp => {
        var config = sp.GetRequiredService<IOptionsMonitor<TcpConnectConfig>>().Get(sorterOptionsName);
        var sorterLogger = sp.GetRequiredService<ILogger<DefaultSorter>>();
        return new DefaultSorter(config, sorterLogger);
    });
    builder.Services.AddHostedService<SortingServer>();
    // 日志清理服务
    builder.Services.AddHostedService<LogCleanupService>();
    // 图片文件监控服务
    builder.Services.AddHostedService<FileSystemImageMonitoringHostedService>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex) {
    // 兜底日志，避免启动阶段失败时无日志
    bootstrapLogger.Error(ex, "程序启动失败");
    throw;
}
finally {
    LogManager.Shutdown();
}
