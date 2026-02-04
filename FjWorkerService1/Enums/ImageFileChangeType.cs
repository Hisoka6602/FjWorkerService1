using System;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Enums {

    /// <summary>
    /// 图片文件变更类型
    /// </summary>
    public enum ImageFileChangeType {

        /// <summary>
        /// 新增
        /// </summary>
        [Description("新增")]
        Created = 1,

        /// <summary>
        /// 修改
        /// </summary>
        [Description("修改")]
        Changed = 2,

        /// <summary>
        /// 删除
        /// </summary>
        [Description("删除")]
        Deleted = 3
    }
}
