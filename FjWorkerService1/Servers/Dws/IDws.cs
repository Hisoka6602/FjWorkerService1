namespace FjWorkerService1.Servers.Dws {

    public interface IDws : IDisposable {

        /// <summary>
        /// 连接DWS
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 接收到消息事件（原始文本）
        /// </summary>
        event EventHandler<string>? MessageReceived;

        /// <summary>
        /// 断开连接事件
        /// </summary>
        event EventHandler? Disconnected;

        /// <summary>
        /// 当前连接状态
        /// </summary>
        bool IsConnected { get; }
    }
}
