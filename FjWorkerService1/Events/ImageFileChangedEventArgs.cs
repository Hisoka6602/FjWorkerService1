using System;
using System.Linq;
using System.Text;
using FjWorkerService1.Enums;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Events {
    /// <summary>
    /// 图片文件变更事件载荷
    /// </summary>
    public readonly record struct ImageFileChangedEventArgs {
        /// <summary>
        /// 监控目录路径
        /// </summary>
        public required string DirectoryPath { get; init; }

        /// <summary>
        /// 文件完整路径
        /// </summary>
        public required string FullPath { get; init; }

        /// <summary>
        /// 变更类型
        /// </summary>
        public required ImageFileChangeType ChangeType { get; init; }
    }
}
