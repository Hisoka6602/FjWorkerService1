using System;
using System.Linq;
using System.Text;
using FjWorkerService1.Enums;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FjWorkerService1.Models.Conf {

    public class TcpConnectConfig {

        //模式(客户端/服务端)
        public required ConnectType Mode { get; init; }

        public required string Ip { get; init; }
        public required int Port { get; init; }
    }
}
