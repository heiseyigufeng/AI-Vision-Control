using MES_WPF.Models;
using SFCDLL_CommonEx.Interface;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace MES_WPF.Services
{
    public class SFCDLLService
    {
        SFCDLL sfcdll;

        public SFCDLLService()
        {
            // AppDomain.CurrentDomain.BaseDirectory 获取当前程序的运行目录（自带结尾的斜杠）
            string path_ini = System.AppDomain.CurrentDomain.BaseDirectory + "SFCDLL_CommonEx.ini";

            sfcdll = new SFCDLL(path_ini);
        }

        private string InsertDataToDataBase(DataTable dt)
        {
            string result = "";


            // 插入数据
            string values = string.Format(
                "{0},{1},{2},Label_Recheck,Label_Recheck,{3},PASS,PASS,{4},{5},PASS,{6},{7}",
                dt.Rows[0]["Model_No"].ToString(),
                dt.Rows[0]["WO_NO"].ToString(),
                dt.Rows[0]["LINE_Code"].ToString(),
                dt.Rows[0]["BARCODE"].ToString(),
                dt.Rows[0]["PhysicalLineCode"].ToString(),
                dt.Rows[0]["BARCODE_Part"].ToString()
            );

            string s1 = sfcdll.InsertIntoTable(
                "RAZER",
                "MES_Transfer_A_ATE_Test",
                "MODEL_NO,WO_NO,LINE_CODE,STATION_CODE,PROCESS_NO,BARCODE,First_Result,Last_Result,PhysicalLineCode,PID,RESULT,IS_AI,CONFIDENCE",
                values
            );

            // 检查插入结果是否成功
            if (s1 != "Y")  // 根据实际的返回结果调整
            {
                result = $"barcode {dt.Rows[0]["BARCODE"].ToString()} insert fail ：{s1}";
                return result;
            }
            else
            {
                result = "Y";
                return result;
            }


        }
    }
}
