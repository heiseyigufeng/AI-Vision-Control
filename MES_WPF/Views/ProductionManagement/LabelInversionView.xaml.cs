using MES_WPF.Model.BasicInformation;
using MES_WPF.Services;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using OpenCvSharp;
using SFCDLL_Common.smtService;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MES_WPF.Views.ProductionManagement
{
    /// <summary>
    /// LabelInversionView.xaml 的交互逻辑
    /// </summary>
    public partial class LabelInversionView : UserControl
    {
        // ========== 路径字段 ==========
        private string _currentPhotoPath = "";
        private string _currentResultPath = "";
        private string _currentSavedOrigPath = "";

        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string AssetsDir = System.IO.Path.Combine(BaseDir, "Assets");
        private static readonly string OrigImageDir = System.IO.Path.Combine(BaseDir, "OrigImage");
        private static readonly string ResultImageDir = System.IO.Path.Combine(BaseDir, "ResultImage");
        private static readonly string LogDir = System.IO.Path.Combine(BaseDir, "logs");

        // =====获取工单和路由信息和站别信息；

        // ========== YOLO ==========
        private string _modelPath = "";
        private string _yamlPath = "";
        private YoloOnnxDetector _detector;

        // ========== 相机（DI 单例） ==========
        private readonly CameraService _cameraService;
        private System.Windows.Threading.DispatcherTimer _previewTimer;
        private Mat _lastCameraFrame;

        // ========== 操作日志 ==========
        public ObservableCollection<LogItem> OperationLogs { get; } = new ObservableCollection<LogItem>();




        // ===================MES Data =======================

        string model_no = "";
        string workOrder = "";
        string line_code = "";
        string station_code = "";
        string process_no = "";
        string barcode = "";
        string fisrt_Result = "";
        string lastR_Result = "";
        string physicalLineCode = "";
        string subbarcode = null;
        string result = "";
        string is_ai = "";
        float confThreshold = 0.66f;
        string route_no = "";

        string okClass = "";
        string ngClass = "";


        // =================== SFC DLL Service ====================
        SFCDLLService sFCDLLService = new SFCDLLService();


        // ==================== 构造函数 ====================


        public LabelInversionView(CameraService cameraService)
        {
            InitializeComponent();

            _cameraService = cameraService ?? new CameraService();

            DataContext = this;

            InitializeFolders();

            _modelPath = FindLatestOnnxFile();
            _yamlPath = FindLatestYamlFile();

            GenerateDetectionClassFromYaml();
            LoadDetectionClasses();
            InitializeYolo();

            Loaded += (s, e) =>
            {
                if (_cameraService.IsOpened)
                    StartPreview();
            };

            Unloaded += (s, e) =>
            {
                StopPreview();
                _lastCameraFrame?.Dispose();
                _lastCameraFrame = null;
            };
        }

        // ==================== 日志项 ====================

        public class LogItem
        {
            public string Time { get; set; }
            public string Status { get; set; }
            public string Detail { get; set; }
        }

        // ==================== 相机控制 ====================

        private void Btn_OpenCamera_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_cameraService.IsOpened)
                {
                    StartPreview();
                    AddLog("OK", "Camera already opened.");
                    return;
                }

                bool ok = _cameraService.Open();
                if (!ok)
                {
                    // 显示具体原因
                    AddLog("NG", $"Failed to open camera: {_cameraService.LastError}");
                    return;
                }

                StartPreview();
                AddLog("OK", "Camera opened.");
            }
            catch (Exception ex)
            {
                AddLog("NG", $"Open camera exception: {ex.Message}");
            }
        }

        private void Btn_CloseCamera_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StopPreview();
                _cameraService.Close();

                CameraImage.Source = null;
                CameraPlaceholder.Visibility = Visibility.Visible;

                AddLog("OK", "Camera closed.");
            }
            catch (Exception ex)
            {
                AddLog("NG", $"Close camera exception: {ex.Message}");
            }
        }

        private void StartPreview()
        {
            CameraPlaceholder.Visibility = Visibility.Collapsed;

            if (_previewTimer != null) return;

            _previewTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _previewTimer.Tick += (s, e) => UpdatePreview();
            _previewTimer.Start();
        }

        private void StopPreview()
        {
            if (_previewTimer != null)
            {
                _previewTimer.Stop();
                _previewTimer = null;
            }
        }

        private void UpdatePreview()
        {
            try
            {
                var frame = _cameraService.GrabFrame();
                if (frame == null) return;

                _lastCameraFrame?.Dispose();
                _lastCameraFrame = frame;

                var bitmap = MatToBitmapSource(frame);
                if (bitmap != null)
                    CameraImage.Source = bitmap;
            }
            catch { }
        }

        // ==================== 文件查找 ====================

        private string FindLatestOnnxFile()
        {
            try
            {
                if (!Directory.Exists(AssetsDir)) return "";
                var files = Directory.GetFiles(AssetsDir, "*.onnx")
                    .OrderByDescending(f => File.GetLastWriteTime(f)).ToList();
                return files.Count > 0 ? files[0] : "";
            }
            catch { return ""; }
        }

        private string FindLatestYamlFile()
        {
            try
            {
                if (!Directory.Exists(AssetsDir)) return "";
                var files = Directory.GetFiles(AssetsDir, "*.yaml")
                    .Concat(Directory.GetFiles(AssetsDir, "*.yml"))
                    .OrderByDescending(f => File.GetLastWriteTime(f)).ToList();
                return files.Count > 0 ? files[0] : "";
            }
            catch { return ""; }
        }

        // ==================== YAML ====================

        private void GenerateDetectionClassFromYaml()
        {
            try
            {
                if (string.IsNullOrEmpty(_yamlPath) || !File.Exists(_yamlPath)) return;

                var classNames = ParseNamesFromYaml(_yamlPath);
                if (classNames.Count == 0) return;

                string txtPath = System.IO.Path.Combine(AssetsDir, "DetectionClass.txt");
                File.WriteAllLines(txtPath, classNames);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to generate DetectionClass.txt: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private List<string> ParseNamesFromYaml(string yamlPath)
        {
            var result = new List<string>();
            var lines = File.ReadAllLines(yamlPath);

            bool inNamesSection = false;
            var rawEntries = new List<(int index, string name)>();

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd();

                if (line.Trim().Equals("names:", StringComparison.OrdinalIgnoreCase))
                {
                    inNamesSection = true;
                    continue;
                }

                if (!inNamesSection) continue;

                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                if (!rawLine.StartsWith(" ") && !rawLine.StartsWith("\t")) break;

                if (trimmed.Contains(':'))
                {
                    var parts = trimmed.Split(new[] { ':' }, 2);
                    if (int.TryParse(parts[0].Trim(), out int idx))
                    {
                        string name = parts[1].Trim().Trim('"', '\'');
                        if (!string.IsNullOrEmpty(name))
                            rawEntries.Add((idx, name));
                    }
                }
                else if (trimmed.StartsWith("-"))
                {
                    string name = trimmed.Substring(1).Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(name))
                        result.Add(name);
                }
            }

            if (rawEntries.Count > 0)
                result = rawEntries.OrderBy(e => e.index).Select(e => e.name).ToList();

            return result;
        }

        // ==================== 初始化 ====================

        private void InitializeYolo()
        {
            try
            {
                if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
                {
                    MessageBox.Show("No .onnx file found in Assets folder.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var classNames = cmb_DetectionClass.Items
                    .Cast<object>().Select(o => o.ToString()).ToArray();

                if (classNames.Length == 0) return;

                _detector = new YoloOnnxDetector(_modelPath, classNames);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load YOLO model: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadDetectionClasses()
        {
            try
            {
                string filePath = System.IO.Path.Combine(AssetsDir, "DetectionClass.txt");
                if (!File.Exists(filePath)) return;

                var lines = File.ReadAllLines(filePath)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToList();

                // 清空两个 ComboBox
                cmb_DetectionClass.Items.Clear();
                cmb_DetectionClass_NG.Items.Clear();

                foreach (var line in lines)
                {
                    string className = line;
                    if (line.Contains(':'))
                        className = line.Substring(line.IndexOf(':') + 1).Trim();

                    cmb_DetectionClass.Items.Add(className);
                    cmb_DetectionClass_NG.Items.Add(className);
                }

                if (cmb_DetectionClass.Items.Count > 0)
                    cmb_DetectionClass.SelectedIndex = 0;

                if (cmb_DetectionClass_NG.Items.Count > 0)
                    cmb_DetectionClass_NG.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load detection classes: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InitializeFolders()
        {
            try
            {
                if (!Directory.Exists(OrigImageDir)) Directory.CreateDirectory(OrigImageDir);
                if (!Directory.Exists(ResultImageDir)) Directory.CreateDirectory(ResultImageDir);
                if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            }
            catch { }
        }

        // ==================== Load ====================

        private async void Btn_Load_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Image",
                Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string filePath = openFileDialog.FileName;

                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    PhotoImage.Source = bitmap;
                    PhotoImage.Visibility = Visibility.Visible;
                    PhotoPlaceholder.Visibility = Visibility.Collapsed;

                    _currentPhotoPath = filePath;
                    _currentSavedOrigPath = "";
                    OrigPathText.Text = "Orig Path: " + filePath;

                    // 自动侦测
                    await DetectFromLoadedImageAsync(filePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Load picture fail: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Load 图片后自动侦测
        /// </summary>
        private async Task DetectFromLoadedImageAsync(string imagePath)
        {
            if (_detector == null)
            {
                MessageBox.Show("YOLO model is not loaded.", "Error");
                return;
            }

            // 读两个 ComboBox 的值
            string okClass = cmb_DetectionClass.SelectedItem?.ToString() ?? "";
            string ngClass = cmb_DetectionClass_NG.SelectedItem?.ToString() ?? "";

            if (string.IsNullOrEmpty(okClass) && string.IsNullOrEmpty(ngClass))
            {
                MessageBox.Show("Please select a detection class.", "Notice");
                return;
            }

            // 读取 Confidence
            if (!float.TryParse(TxtConfidence.Text, out confThreshold))
            {
                confThreshold = 0.25f;
            }
            confThreshold = Math.Max(0f, Math.Min(1f, confThreshold));

            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                string resultPath = System.IO.Path.Combine(ResultImageDir, $"result_Load_{timestamp}.jpg");
                string origSavePath = System.IO.Path.Combine(OrigImageDir, $"orig_Load_{timestamp}.jpg");

                try
                {
                    File.Copy(imagePath, origSavePath, true);
                    _currentSavedOrigPath = origSavePath;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save original image: {ex.Message}", "Warning",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                // 传 null，返回所有类别
                DetectionResult detResult = await Task.Run(() =>
                    _detector.Detect(imagePath, resultPath, null, confThreshold));

                // 显示结果图
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(resultPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                ResultImage.Source = bitmap;
                ResultImage.Visibility = Visibility.Visible;
                ResultPlaceholder.Visibility = Visibility.Collapsed;

                _currentResultPath = resultPath;
                ResultPathText.Text = "Result Path: " + resultPath;

                // 判断 OK / NG
                bool finalOK;
                string reason;
                JudgeResult(detResult, okClass, ngClass, out finalOK, out reason);

                AddDetectionLog(finalOK, detResult, $"{okClass} / {ngClass}", reason);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Detection failed: {ex.Message}\n{ex.StackTrace}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== 条码 KeyDown ====================

        private void Txt_Barcode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                Btn_Detect_Click(this.Btn_Detect, new RoutedEventArgs());
            }
        }

        // ==================== 侦测 ====================

        private async void Btn_Detect_Click(object sender, RoutedEventArgs e)
        {
            if (checkCondition())
            {
                AI_Detect("AI");
            }
        }

        private bool checkCondition()
        {
            // ====== 新增：每次点击侦测，先清空界面上上一次的残留图片 ======
            ResultImage.Source = null;
            ResultImage.Visibility = Visibility.Collapsed;
            ResultPlaceholder.Visibility = Visibility.Visible;
            ResultPathText.Text = "Result Path: -";

            // 如果你想连原图也一起清空，取消下面四行的注释：
            PhotoImage.Source = null;
            PhotoImage.Visibility = Visibility.Collapsed;
            PhotoPlaceholder.Visibility = Visibility.Visible;
            OrigPathText.Text = "Orig Path: -";
            // =============================================================

            // ====== 修改这里：判断 CheckBox 是否被勾选 ======
            if (Connect_SFC.IsChecked == true)
            {
                // 如果勾选了，执行 MES 数据库检查及延时
                if (!checkWorkOrderInfo())
                {
                    return false;
                }
            }

            // 1. 检查相机是否打开
            if (!_cameraService.IsOpened)
            {
                AddLog("NG", "Camera not turned on!");
                return false;
            }

            // 2. 检查 YOLO 模型
            if (_detector == null)
            {
                AddLog("NG", "YOLO model is not loaded.");
          
                return false;
            }

            // 3. 读两个 ComboBox 的值
            okClass = cmb_DetectionClass.SelectedItem?.ToString() ?? "";
            ngClass = cmb_DetectionClass_NG.SelectedItem?.ToString() ?? "";

            if (string.IsNullOrEmpty(okClass) && string.IsNullOrEmpty(ngClass))
            {
                AddLog("NG", "Please select a detection class.");
                return false;
            }

            // =========================================================
            // 检查 OK 和 NG 类别是否选择了同一个
            // =========================================================
            if (!string.IsNullOrEmpty(okClass) && !string.IsNullOrEmpty(ngClass) && okClass == ngClass)
            {
                AddLog("NG", "The OK class and NG class cannot be the same!");
                return false;
            }



            // 4. 抓取相机当前帧
            if (_lastCameraFrame == null || _lastCameraFrame.Empty())
            {
                AddLog("NG", "Camera not turned on!");
                return false;
            }

            // 5. 读取 Confidence
            confThreshold = 0.25f;
            if (!float.TryParse(TxtConfidence.Text, out confThreshold))
            {
                confThreshold = 0.25f;
            }
            confThreshold = Math.Max(0f, Math.Min(1f, confThreshold));

            // 6. 读取条码
            barcode = Txt_Barcode.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(barcode))
            {
                AddLog("NG", "Barcode can not input empty!");
                return false;
            }
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            {
                barcode = barcode.Replace(c, '_');
            }

            // 7. 检查是否有延时
            if (double.TryParse(txt_delayTime.Text.Trim(), out double delaySeconds))
            {
                // 2. 将秒转为毫秒
                int delayMs = (int)(delaySeconds * 1000);

                // 3. 强制程序在这里死等（UI会卡住）
                Thread.Sleep(delayMs);

                // 4. 时间到了，现场也准备好了，继续往下执行
            }
            else
            {
                AddLog("NG", "Invalid delay time,Please enter a number!");
                return false;
            }




            // check station info

            if (Connect_SFC.IsChecked == true)
            {

                if (subConvertMain.IsChecked == true)
                {
                    string sfcGetMainBarcode = sFCDLLService.sfcdll.GetSpecialValue("Razer", "GET_BARCODE", barcode);

                    if (string.IsNullOrWhiteSpace(sfcGetMainBarcode))
                    {
                        AddLog("NG", "Failed to retrieve main barcode!");
                        return false;
                    }
                    subbarcode = barcode;
                    barcode = sfcGetMainBarcode;
                }

                string checkStationResult = sFCDLLService.sfcdll.CheckStationPass("Razer", station_code, barcode, line_code, "1.0");

                if (checkStationResult.Trim().ToUpper() != "Y")
                {
                    AddLog("NG", checkStationResult);
                    return false;
                }
            }

            return true;
        }

        private async void AI_Detect(string IsManual)
        {
            try
            {
                Btn_Detect.IsEnabled = false;

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string resultPath = System.IO.Path.Combine(ResultImageDir, $"result_{IsManual}_{barcode}_{timestamp}.jpg");
                string origSavePath = System.IO.Path.Combine(OrigImageDir, $"orig_{barcode}_{timestamp}.jpg");

                // 7. 保存相机帧到原图
                Cv2.ImWrite(origSavePath, _lastCameraFrame);
                _currentPhotoPath = origSavePath;
                _currentSavedOrigPath = origSavePath;

                // 8. 显示到 Photo 区域
                var photoBitmap = MatToBitmapSource(_lastCameraFrame);
                if (photoBitmap != null)
                {
                    PhotoImage.Source = photoBitmap;
                    PhotoImage.Visibility = Visibility.Visible;
                    PhotoPlaceholder.Visibility = Visibility.Collapsed;
                }
                OrigPathText.Text = "Orig Path: " + origSavePath;

                #region 判断需要的参数
                bool finalOK;
                string reason;
                DetectionResult detResult = null;
                #endregion

                if (IsManual == "AI")
                {

                    // 9. 用相机帧做 YOLO 检测（传 null，返回所有类别）
                    detResult = await Task.Run(() =>
                       _detector.Detect(origSavePath, resultPath, null, confThreshold));

                    // 10. 显示结果图
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(resultPath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    ResultImage.Source = bitmap;
                    ResultImage.Visibility = Visibility.Visible;
                    ResultPlaceholder.Visibility = Visibility.Collapsed;

                    _currentResultPath = resultPath;
                    ResultPathText.Text = "Result Path: " + resultPath;

                    // 11. 判定 OK / NG

                    JudgeResult(detResult, okClass, ngClass, out finalOK, out reason);

                    if (Connect_SFC.IsChecked == true)
                    {
                        DataTable dt = CreateEmptyInsertDataTable();

                        // 2. 你自定义所有参数赋值（替换为你实际的变量名）
                        dt.Rows[0]["MODEL_NO"] = model_no;
                        dt.Rows[0]["WO_NO"] = workOrder;
                        dt.Rows[0]["LINE_CODE"] = line_code;
                        dt.Rows[0]["STATION_CODE"] = station_code;
                        dt.Rows[0]["PROCESS_NO"] = station_code;
                        dt.Rows[0]["BARCODE"] = barcode.Trim();
                        if (finalOK)
                        {
                            dt.Rows[0]["First_Result"] = "PASS";
                            dt.Rows[0]["Last_Result"] = "PASS";
                        }
                        else
                        {
                            dt.Rows[0]["First_Result"] = "FAIL";
                            dt.Rows[0]["Last_Result"] = "FAIL";
                        }
                        dt.Rows[0]["PhysicalLineCode"] = physicalLineCode;
                        dt.Rows[0]["Subbarcode"] = subbarcode;
                        dt.Rows[0]["RESULT"] = finalOK ? "PASS" : "FAIL"; // 你的判定逻辑
                        dt.Rows[0]["IS_AI"] = IsManual;
                        dt.Rows[0]["CONFIDENCE"] = confThreshold;


                        string insertData_Result = sFCDLLService.InsertDataToDataBase(dt);

                        if (insertData_Result != "Y")
                        {
                            finalOK = false;
                            reason = "Failed to insert data into database." + reason;
                        }
                    }
                    AddDetectionLog(finalOK, detResult, $"{okClass} / {ngClass}", reason);
                }
                else
                {
                    Cv2.ImWrite(resultPath, _lastCameraFrame);
                    // ====== 新增：把人工检测保存的图片加载到界面显示 ======
                    var bitmapManual = new BitmapImage();
                    bitmapManual.BeginInit();
                    bitmapManual.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapManual.UriSource = new Uri(resultPath, UriKind.Absolute);
                    bitmapManual.EndInit();
                    bitmapManual.Freeze();

                    ResultImage.Source = bitmapManual;
                    ResultImage.Visibility = Visibility.Visible;
                    ResultPlaceholder.Visibility = Visibility.Collapsed;

                    _currentResultPath = resultPath;
                    ResultPathText.Text = "Result Path: " + resultPath;
                    // ==========================================================

                    if (IsManual == "Manual_OK")
                    {
                        finalOK = true;
                        reason = "Manual assessment result is OK.";
                    }
                    else
                    {
                        finalOK = false;
                        reason = "Manual assessment result is NG.";
                    }

                    if (Connect_SFC.IsChecked == true)
                    {
                        DataTable dt = CreateEmptyInsertDataTable();

                        // 2. 你自定义所有参数赋值（替换为你实际的变量名）
                        dt.Rows[0]["MODEL_NO"] = model_no;
                        dt.Rows[0]["WO_NO"] = workOrder;
                        dt.Rows[0]["LINE_CODE"] = line_code;
                        dt.Rows[0]["STATION_CODE"] = station_code;
                        dt.Rows[0]["PROCESS_NO"] = station_code;
                        dt.Rows[0]["BARCODE"] = barcode.Trim();
                        if (finalOK)
                        {
                            dt.Rows[0]["First_Result"] = "PASS";
                            dt.Rows[0]["Last_Result"] = "PASS";
                        }
                        else
                        {
                            dt.Rows[0]["First_Result"] = "FAIL";
                            dt.Rows[0]["Last_Result"] = "FAIL";
                        }
                        dt.Rows[0]["PhysicalLineCode"] = physicalLineCode;
                        dt.Rows[0]["Subbarcode"] = subbarcode;
                        dt.Rows[0]["RESULT"] = finalOK ? "PASS" : "FAIL"; // 你的判定逻辑
                        dt.Rows[0]["IS_AI"] = IsManual;
                        dt.Rows[0]["CONFIDENCE"] = confThreshold;


                        string insertData_Result = sFCDLLService.InsertDataToDataBase(dt);

                        if (insertData_Result != "Y")
                        {
                            finalOK = false;
                            reason = "Failed to insert data into database." + reason;
                        }
                    }

                    AddManualDetectionLog(finalOK, detResult, $"{okClass} / {ngClass}", reason);
                }

               



    
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Detection failed: {ex.Message}\n{ex.StackTrace}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Btn_Detect.IsEnabled = true;
            }
        }

        public DataTable CreateEmptyInsertDataTable()
        {
            DataTable dt = new DataTable();

            // 严格按照你需要的 13 个字段创建列
            dt.Columns.Add("MODEL_NO", typeof(string));
            dt.Columns.Add("WO_NO", typeof(string));
            dt.Columns.Add("LINE_CODE", typeof(string));
            dt.Columns.Add("STATION_CODE", typeof(string));
            dt.Columns.Add("PROCESS_NO", typeof(string));
            dt.Columns.Add("BARCODE", typeof(string));
            dt.Columns.Add("First_Result", typeof(string));
            dt.Columns.Add("Last_Result", typeof(string));
            dt.Columns.Add("PhysicalLineCode", typeof(string));
            dt.Columns.Add("Subbarcode", typeof(string));
            dt.Columns.Add("RESULT", typeof(string));
            dt.Columns.Add("IS_AI", typeof(string));
            dt.Columns.Add("CONFIDENCE", typeof(string));

            // 直接创建第一行空数据并加入
            DataRow row = dt.NewRow();
            dt.Rows.Add(row);

            return dt;
        }
        private bool checkWorkOrderInfo()
        {
            bool check_result = false;
            // read the work order 
            workOrder = Txt_WorkOrder.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(workOrder))
            {
                AddLog("NG", "workorder can not input empty!");
                return check_result;
            }
            station_code = ctxt_stations.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(station_code))
            {
                AddLog("NG", "station can not input empty!");
                return check_result;
            }
            check_result = true;
            return check_result;


        }

        /// <summary>
        /// 统一判定 OK / NG：
        /// - 检测到 NG 类 → NG
        /// - 检测到 OK 类且无 NG → OK
        /// - 都没检测到 → NG
        /// </summary>
        private void JudgeResult(DetectionResult detResult, string okClass, string ngClass,
                                 out bool finalOK, out string reason)
        {
            var detectedClasses = detResult.ValidDetections
                .Select(d => d.ClassName)
                .ToList();

            bool hasNG = !string.IsNullOrEmpty(ngClass) &&
                         detectedClasses.Any(c => c.Equals(ngClass, StringComparison.OrdinalIgnoreCase));

            bool hasOK = !string.IsNullOrEmpty(okClass) &&
                         detectedClasses.Any(c => c.Equals(okClass, StringComparison.OrdinalIgnoreCase));

            if (hasNG)
            {
                finalOK = false;
                reason = $"Detected NG class: {ngClass}";
            }
            else if (hasOK)
            {
                finalOK = true;
                reason = $"Detected OK class: {okClass}";
            }
            else
            {
                finalOK = false;
                reason = "Neither OK nor NG class detected";
            }
        }

        // ==================== 日志 ====================

        private void AddLog(string status, string detail)
        {
            string time = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");

            OperationLogs.Insert(0, new LogItem
            {
                Time = time,
                Status = status,
                Detail = detail
            });

            try
            {
                string logFile = System.IO.Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(logFile, $"{time}\t{status}\t{detail}{Environment.NewLine}");
            }
            catch { }
        }



        private void AddManualDetectionLog(bool found, DetectionResult detResult,
                             string selectedClass, string customReason = null)
        {
            string status = found ? "OK" : "NG";
            string detail;
            string baseInfo="Manual Detection";
            // 拼接自定义 reason 和详细信息
            if (!string.IsNullOrEmpty(customReason))
            {
                detail = $"{customReason} | {baseInfo}";
            }
            else
            {
                detail = baseInfo;
            }
            AddLog(status, detail);
        }

        /// <summary>
        /// 添加一条检测日志
        /// </summary>
        private void AddDetectionLog(bool found, DetectionResult detResult,
                             string selectedClass, string customReason = null)
        {
            string status = found ? "OK" : "NG";
            string detail;

            // 先算出详细信息
            int imgW = 0, imgH = 0;
            if (File.Exists(_currentPhotoPath))
            {
                using var mat = Cv2.ImRead(_currentPhotoPath);
                imgW = mat.Width;
                imgH = mat.Height;
            }

            string baseInfo;
            
            if (found && detResult.ValidDetections.Count > 0)
            {
                var best = detResult.ValidDetections.OrderByDescending(d => d.Confidence).First();
                int cx = best.X + best.Width / 2;
                int cy = best.Y + best.Height / 2;

                baseInfo = $"Class: {best.ClassName}, Conf: {best.Confidence:F2}, " +
                           $"ImgSize: {imgW}x{imgH}, Center: ({cx},{cy})";
            }
            else if (detResult.LowConfDetections.Count > 0)
            {
                var best = detResult.LowConfDetections.OrderByDescending(d => d.Confidence).First();
                int cx = best.X + best.Width / 2;
                int cy = best.Y + best.Height / 2;

                baseInfo = $"[Confidence below threshold] Class: {best.ClassName}, Conf: {best.Confidence:F2}, " +
                           $"ImgSize: {imgW}x{imgH}, Center: ({cx},{cy})";
            }
            else
            {
                baseInfo = "Not detected: target not found";
            }

            // 拼接自定义 reason 和详细信息
            if (!string.IsNullOrEmpty(customReason))
            {
                detail = $"{customReason} | {baseInfo}";
            }
            else
            {
                detail = baseInfo;
            }

            AddLog(status, detail);
        }

        // ==================== Open 按钮 ====================

        private void Btn_Open_Click(object sender, RoutedEventArgs e)
        {
            string pathToOpen = !string.IsNullOrEmpty(_currentSavedOrigPath)
                ? _currentSavedOrigPath
                : _currentPhotoPath;
            OpenFolderAndSelectFile(pathToOpen);
        }

        private void Btn_OpenResult_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderAndSelectFile(_currentResultPath);
        }

        private void OpenFolderAndSelectFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show("File does not exist: " + filePath, "Message",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open the folder: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== Mat → BitmapSource ====================

        private System.Windows.Media.Imaging.BitmapSource MatToBitmapSource(Mat mat)
        {
            try
            {
                using var bgra = new Mat();
                Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);

                int width = bgra.Width;
                int height = bgra.Height;
                int stride = (int)bgra.Step();
                byte[] data = new byte[stride * height];

                System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);

                return System.Windows.Media.Imaging.BitmapSource.Create(
                    width, height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null, data, stride);
            }
            catch
            {
                return null;
            }
        }


        private void Txt_WorkOrder_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;

                workOrder = Txt_WorkOrder.Text.Trim();

                if (string.IsNullOrEmpty(workOrder))
                {
                    AddLog("NG", "work order can not input empty!");
                    return;
                }

                try
                {
                    var mesService = new MesDataService();
                    var info = mesService.GetWoInfo(workOrder);

                    if (info != null)
                    {
                        //更新界面的文本(请替换为你界面上实际控件的 x: Name)
                        txt_model.Text = info.Model;
                        txt_routeNO.Text = info.RouteNo;
                        txt_lineCode.Text = info.LineCode;

                        model_no = info.Model;
                        route_no = info.RouteNo;
                        line_code = info.LineCode;

                        // 将站点列表绑定到 ComboBox
                        ctxt_stations.ItemsSource = null;
                        ctxt_stations.ItemsSource = info.ProcessList;

                        // 如果列表不为空，默认选中第一项
                        if (info.ProcessList.Count > 0)
                        {
                            ctxt_stations.SelectedIndex = 0;
                            station_code = info.ProcessList[0];
                        }

                        line_code = info.LineCode;
                        physicalLineCode = info.PhysicalLineCode;

                    }
                    else
                    {
                        AddLog("NG", "No information found for this Work Order. Please check the ID.");
                    }
                }
                catch (Exception ex)
                {
                    AddLog("NG", $"An error occurred: {ex.Message}");
                }
            }
        }

        private void subConvertMain_Checked(object sender, RoutedEventArgs e)
        {
            if (Connect_SFC.IsChecked != true)
            {
                subConvertMain.IsChecked = false;
                AddLog("NG", "Please connect to SFC first.");
            }
        }

        private void Connect_SFC_Click(object sender, RoutedEventArgs e)
        {
            if (Connect_SFC.IsChecked == false)
            {
                subConvertMain.IsChecked = false;
            }
        }

        private void Btn_ManualNG_Click(object sender, RoutedEventArgs e)
        {
            if (checkCondition())
            {
                AI_Detect("Manual_NG");
            }
        }

        private void Btn_Manual_OK_Click(object sender, RoutedEventArgs e)
        {
            if (checkCondition())
            {
                AI_Detect("Manual_OK");
            }
        }
    }
}