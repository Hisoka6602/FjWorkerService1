using FjWorkerService1.Models.Sorting;

namespace FjWorkerService1.Servers.Sorter {

    /// <summary>
    /// 排序系统接口：用于连接、接收事件、发送事件、发送格口指令。
    /// </summary>
    public interface ISorter : IDisposable {

        /// <summary>
        /// 断开连接事件
        /// </summary>
        event EventHandler? Disconnected;

        /// <summary>
        /// 连接到排序系统。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        Task ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 当收到排序系统的事件/消息时触发。
        /// </summary>
        event EventHandler<SortingCompletedMessage>? SortingCompleted;

        /// <summary>
        /// 创建包裹
        /// </summary>
        event EventHandler<ParcelDetectedMessage>? ParcelDetected;

        /// <summary>
        /// 向排序系统发送事件。
        /// </summary>
        /// <param name="chuteAssignment"></param>
        /// <param name="cancellationToken">取消令牌。</param>
        Task SendEventAsync(ChuteAssignmentEventArgs chuteAssignment, CancellationToken cancellationToken = default);
    }
}
