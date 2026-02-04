using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Events {
    /// <summary>
    /// 图片文件重命名事件载荷
    /// </summary>
    public readonly record struct ImageFileRenamedEventArgs {
        /// <summary>
        /// 监控目录路径
        /// </summary>
        public required string DirectoryPath { get; init; }

        /// <summary>
        /// 新文件完整路径
        /// </summary>
        public required string FullPath { get; init; }

        /// <summary>
        /// 旧文件完整路径
        /// </summary>
        public required string OldFullPath { get; init; }
    }
}
