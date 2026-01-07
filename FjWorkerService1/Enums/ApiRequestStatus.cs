using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Enums {

    public enum ApiRequestStatus {

        /// <summary>
        /// 成功 / Success
        /// </summary>
        Success = 0,

        /// <summary>
        /// 失败 / Failure
        /// </summary>
        Failure = 1,

        /// <summary>
        /// 异常 / Exception
        /// </summary>
        Exception = 2,

        /// <summary>
        /// 超时 / Timeout
        /// </summary>
        Timeout = 3
    }
}
