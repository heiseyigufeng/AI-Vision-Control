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
    public  class SFCDLLService
    {
       public SFCDLL sfcdll;

        public   SFCDLLService()
        {
            // AppDomain.CurrentDomain.BaseDirectory 获取当前程序的运行目录（自带结尾的斜杠）
            string path_ini = System.AppDomain.CurrentDomain.BaseDirectory + "\\Libs\\SFCDLLSFCDLL_CommonEx.ini";

            sfcdll = new SFCDLL(path_ini);
        }
        public SFCDLLService( string path_ini)
        {
            sfcdll = new SFCDLL(path_ini);
        }
     
        public string InsertDataToDataBase(DataTable dt)
        {
            string result = "";


            // 插入数据
            string values = string.Format(
                "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12}",
                dt.Rows[0]["Model_No"].ToString(),
                dt.Rows[0]["WO_NO"].ToString(),
                dt.Rows[0]["LINE_Code"].ToString(),
                dt.Rows[0]["STATION_Code"].ToString(),
                dt.Rows[0]["PROCESS_No"].ToString(),
                dt.Rows[0]["BARCODE"].ToString(),
                dt.Rows[0]["First_Result"].ToString(),
                dt.Rows[0]["Last_Result"].ToString(),
                dt.Rows[0]["PhysicalLineCode"].ToString(),
                dt.Rows[0]["Subbarcode"].ToString(),
                dt.Rows[0]["RESULT"].ToString(),
                dt.Rows[0]["IS_AI"].ToString(),
                dt.Rows[0]["CONFIDENCE"].ToString()
            );

            string s1 = sfcdll.InsertIntoTable(
                "RAZER",
                "MES_Transfer_A_ATE_Test",
                "MODEL_NO,WO_NO,LINE_CODE,STATION_CODE,PROCESS_NO,BARCODE,First_Result,Last_Result,PhysicalLineCode,Subbarcode,RESULT,IS_AI,CONFIDENCE",
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
