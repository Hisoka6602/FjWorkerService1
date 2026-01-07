using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Helpers {

    /// <summary>
    /// 任务观察扩展：确保异常被记录，避免静默失败
    /// </summary>
    internal static class TaskObserveExtensions {

        public static void Observe(this Task task, ILogger logger, string taskName) {
            _ = task.ContinueWith(
                static (t, state) => {
                    var (innerLogger, innerName) = ((ILogger, string))state!;

                    if (t.IsFaulted && t.Exception is not null) {
                        innerLogger.LogError(t.Exception, "{TaskName} 发生异常", innerName);
                        return;
                    }

                    if (t.IsCanceled) {
                        innerLogger.LogInformation("{TaskName} 已取消", innerName);
                    }
                },
                (logger, taskName),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
