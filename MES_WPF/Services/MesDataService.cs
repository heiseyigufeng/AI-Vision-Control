using SqlSugar;
using System;
using System.Linq;
using MES_WPF.Models;

namespace MES_WPF.Services
{
    public class MesDataService
    {
        private readonly string queryConnectString = "Server = 10.80.1.178; Database = SFCS_TH_Assembly_Stage; User Id = sfcreport;Password = rpt.abc654;Encrypt=True;TrustServerCertificate=True;";

        public WoInfoResult GetWoInfo(string workOrder)
        {
            if (string.IsNullOrWhiteSpace(workOrder)) {
                return null;
            }


            var db = new SqlSugarClient(new ConnectionConfig()
                {
                    ConnectionString = queryConnectString,
                    DbType = DbType.SqlServer,
                    IsAutoCloseConnection = true
                });

                var result = new WoInfoResult();

                // 1. 查机种(Model) 和 路由(RouteNo)
                string sql1 = @"
                    SELECT TOP 1 
                        T.MODEL_NO AS Model, 
                        T.ROUTE_NO AS RouteNo 
                    FROM MES_WO T WITH (NOLOCK) 
                    WHERE Work_Order = @workOrder";

                var woInfo = db.Ado.SqlQuery<WoInfoResult>(sql1, new { workOrder }).FirstOrDefault();
                if (woInfo == null) return null;

                result.Model = woInfo.Model;
                result.RouteNo = woInfo.RouteNo;

                // 2. 查多个站点(ProcessNo)
                string sql2 = @"
                    SELECT T.PROCESS_NO 
                    FROM SYS_ROUTE_DETAIL T WITH (NOLOCK)
                    WHERE T.ROUTE_NO = @routeNo
                        AND LEFT(T.PROCESS_NO, 4) = 'OBD_'";

                // 直接查出所有 string 类型的集合
                var processes = db.Ado.SqlQuery<string>(sql2, new { routeNo = result.RouteNo });
                if (processes != null && processes.Count > 0)
                {
                    result.ProcessList = processes;
                }

                // 3. 查线别(LineCode)
                string sql3 = @"
                    SELECT TOP 1 T.LINE_CODE AS LineCode ,T.PhysicalLineCode
                    FROM MES_MAIN_LIST T WITH (NOLOCK) 
                    WHERE T.WO_NO = @workOrder AND T.STATION_CODE = 'INPUT'";

                var lineInfo = db.Ado.SqlQuery<WoInfoResult>(sql3, new { workOrder }).FirstOrDefault();
                if (lineInfo != null)
                {
                    result.LineCode = lineInfo.LineCode;
                    result.PhysicalLineCode = lineInfo.PhysicalLineCode;
                }

                return result;
           
              
           
        }
    }
}