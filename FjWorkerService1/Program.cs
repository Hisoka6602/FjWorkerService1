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

var nlogConfigPath = Path.Combine(AppContext.BaseDirectory, "nlog.config");
LogManager.Setup().LoadConfigurationFromFile(nlogConfigPath);
var bootstrapLogger = LogManager.GetCurrentClassLogger();

try {
    var builder = Host.CreateApplicationBuilder(args);

    // Windows 服务
    builder.Services.AddWindowsService(options => { options.ServiceName = "FjWorkerService"; });

    // 切换到 NLog
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Logging.AddNLog();

    // ======= 下面保留你现有的 DI 注册（原样） =======
    builder.Services.Configure<LogCleanupSettings>(
        builder.Configuration.GetSection("LogCleanup"));
    builder.Services.AddSingleton<SafeExecutor>();
    builder.Services
        .AddHttpClient<IWcs, PostProcessingCenterApiClient>((sp, client) => {
            var configuration = sp.GetRequiredService<IConfiguration>();

            var url = configuration.GetValue<string>("PostProcessingCenterConfig:Url");
            if (string.IsNullOrWhiteSpace(url)) {
                throw new InvalidOperationException("配置缺失：PostProcessingCenterConfig:Url");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)) {
                throw new InvalidOperationException($"配置无效：PostProcessingCenterConfig:Url = {url}");
            }

            client.BaseAddress = baseUri;
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

    const string DwsOptionsName = "Dws";
    const string SorterOptionsName = "Sorter";

    builder.Services
        .AddOptions<TcpConnectConfig>(DwsOptionsName)
        .Bind(builder.Configuration.GetSection("DwsTcpConnectConfig"))
        .Validate(c => !string.IsNullOrWhiteSpace(c.Ip), "配置无效：DwsTcpConnectConfig:Ip 不能为空")
        .Validate(c => c.Port is > 0 and <= 65535, "配置无效：DwsTcpConnectConfig:Port 范围必须为 1-65535")
        .ValidateOnStart();

    builder.Services
        .AddOptions<TcpConnectConfig>(SorterOptionsName)
        .Bind(builder.Configuration.GetSection("SorterTcpConnectConfig"))
        .Validate(c => !string.IsNullOrWhiteSpace(c.Ip), "配置无效：SorterTcpConnectConfig:Ip 不能为空")
        .Validate(c => c.Port is > 0 and <= 65535, "配置无效：SorterTcpConnectConfig:Port 范围必须为 1-65535")
        .ValidateOnStart();

    builder.Services.AddSingleton<IDws>(sp => {
        var config = sp.GetRequiredService<IOptionsMonitor<TcpConnectConfig>>().Get(DwsOptionsName);
        var dwsLogger = sp.GetRequiredService<ILogger<DefaultDws>>();
        return new DefaultDws(config, dwsLogger);
    });

    builder.Services.AddSingleton<ISorter>(sp => {
        var config = sp.GetRequiredService<IOptionsMonitor<TcpConnectConfig>>().Get(SorterOptionsName);
        var sorterLogger = sp.GetRequiredService<ILogger<DefaultSorter>>();
        return new DefaultSorter(config, sorterLogger);
    });
    builder.Services.AddHostedService<SortingServer>();
    //日志清理服务
    builder.Services.AddHostedService<LogCleanupService>();
    var host = builder.Build();
    host.Run();
}
catch (Exception ex) {
    // 这里必须兜底，否则服务启动失败时没日志
    bootstrapLogger.Error(ex, "服务启动失败");
    throw;
}
finally {
    LogManager.Shutdown();
}
