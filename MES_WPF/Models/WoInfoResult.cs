using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MES_WPF.Models
{
     public class WoInfoResult
        {
            public string Model { get; set; }     // 机种
            public string RouteNo { get; set; }   // 路由号
            public string LineCode { get; set; }  // 线别
        public string PhysicalLineCode { get; set; }  // 实体线别

        // 站点可能有多个，使用 List<string> 接收
        public System.Collections.Generic.List<string> ProcessList { get; set; } = new System.Collections.Generic.List<string>();
        }
    
}
