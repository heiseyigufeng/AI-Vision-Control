using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MES_WPF.Services;

namespace MES_WPF.Views.ProductionManagement
{
    /// <summary>
    /// LabelInversionView.xaml 的交互逻辑
    /// </summary>
    public partial class LabelInversionView : UserControl
    {
        // ========== 路径字段 ==========
        private string _currentPhotoPath = "";        // Load 选的原始路径
        private string _currentResultPath = "";       // 结果图路径
        private string _currentSavedOrigPath = "";    // 保存到 OrigImage 后的原图路径

        // 程序所在目录 + 子文件夹
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string AssetsDir = Path.Combine(BaseDir, "Assets");
        private static readonly string OrigImageDir = Path.Combine(BaseDir, "OrigImage");
        private static readonly string ResultImageDir = Path.Combine(BaseDir, "ResultImage");
        private static readonly string LogDir = Path.Combine(BaseDir, "logs");

        // ========== YOLO 相关（运行时动态查找） ==========
        private string _modelPath = "";   // 最新的 .onnx
        private string _yamlPath = "";    // 最新的 .yaml / .yml
        private YoloOnnxDetector _detector;

        // ========== 操作日志 ==========
        public ObservableCollection<LogItem> OperationLogs { get; } = new ObservableCollection<LogItem>();

        public LabelInversionView()
        {
            InitializeComponent();

            DataContext = this;

            InitializeFolders();

            // 查找最新的 onnx 和 yaml
            _modelPath = FindLatestOnnxFile();
            _yamlPath = FindLatestYamlFile();

            // 从 yaml 生成 DetectionClass.txt
            GenerateDetectionClassFromYaml();

            // 加载类别到 ComboBox
            LoadDetectionClasses();

            // 初始化 YOLO
            InitializeYolo();
        }

        public class LogItem
        {
            public string Time { get; set; }
            public string Status { get; set; }
            public string Detail { get; set; }
        }

        // ==================== 文件查找 ====================

        /// <summary>
        /// 查找 Assets 文件夹下最新的 .onnx 文件（按修改时间倒序）
        /// </summary>
        private string FindLatestOnnxFile()
        {
            try
            {
                if (!Directory.Exists(AssetsDir)) return "";

                var files = Directory.GetFiles(AssetsDir, "*.onnx")
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                return files.Count > 0 ? files[0] : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 查找 Assets 文件夹下最新的 .yaml / .yml 文件（按修改时间倒序）
        /// </summary>
        private string FindLatestYamlFile()
        {
            try
            {
                if (!Directory.Exists(AssetsDir)) return "";

                var files = Directory.GetFiles(AssetsDir, "*.yaml")
                    .Concat(Directory.GetFiles(AssetsDir, "*.yml"))
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                return files.Count > 0 ? files[0] : "";
            }
            catch
            {
                return "";
            }
        }

        // ==================== YAML 解析 ====================

        /// <summary>
        /// 从最新的 yaml 生成 DetectionClass.txt（存在则覆盖）
        /// </summary>
        private void GenerateDetectionClassFromYaml()
        {
            try
            {
                if (string.IsNullOrEmpty(_yamlPath) || !File.Exists(_yamlPath))
                {
                    MessageBox.Show("No .yaml file found in Assets folder.", "Warning",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var classNames = ParseNamesFromYaml(_yamlPath);

                if (classNames.Count == 0)
                {
                    MessageBox.Show($"No class names found in {Path.GetFileName(_yamlPath)}.", "Warning",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string txtPath = Path.Combine(AssetsDir, "DetectionClass.txt");
                File.WriteAllLines(txtPath, classNames);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to generate DetectionClass.txt: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 从 YAML 文件解析 names 段
        /// 支持：
        /// names:
        ///   0: BoRui
        ///   1: BaBa
        /// 或
        /// names:
        ///   - BoRui
        ///   - BaBa
        /// </summary>
        private List<string> ParseNamesFromYaml(string yamlPath)
        {
            var result = new List<string>();
            var lines = File.ReadAllLines(yamlPath);

            bool inNamesSection = false;
            var rawEntries = new List<(int index, string name)>();

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd();

                // 找 "names:"
                if (line.Trim().Equals("names:", StringComparison.OrdinalIgnoreCase))
                {
                    inNamesSection = true;
                    continue;
                }

                if (!inNamesSection) continue;

                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                // 缩进结束 → 退出 names 段
                if (!rawLine.StartsWith(" ") && !rawLine.StartsWith("\t"))
                    break;

                // 格式 1: "0: BoRui"
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
                // 格式 2: "- BoRui"
                else if (trimmed.StartsWith("-"))
                {
                    string name = trimmed.Substring(1).Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(name))
                        result.Add(name);
                }
            }

            // 格式 1 按索引排序返回
            if (rawEntries.Count > 0)
            {
                result = rawEntries.OrderBy(e => e.index).Select(e => e.name).ToList();
            }

            return result;
        }

        // ==================== 初始化 ====================

        /// <summary>
        /// 初始化 YOLO
        /// </summary>
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
                    .Cast<object>()
                    .Select(o => o.ToString())
                    .ToArray();

                if (classNames.Length == 0)
                {
                    MessageBox.Show("No detection class loaded.", "Warning",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _detector = new YoloOnnxDetector(_modelPath, classNames);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load YOLO model: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 加载检测类别
        /// </summary>
        private void LoadDetectionClasses()
        {
            try
            {
                string filePath = Path.Combine(AssetsDir, "DetectionClass.txt");

                if (!File.Exists(filePath))
                {
                    MessageBox.Show($"DetectionClass.txt not found: {filePath}", "Warning",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var lines = File.ReadAllLines(filePath)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToList();

                cmb_DetectionClass.Items.Clear();

                foreach (var line in lines)
                {
                    string className = line;
                    if (line.Contains(':'))
                        className = line.Substring(line.IndexOf(':') + 1).Trim();

                    cmb_DetectionClass.Items.Add(className);
                }

                if (cmb_DetectionClass.Items.Count > 0)
                    cmb_DetectionClass.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load detection classes: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 创建文件夹
        /// </summary>
        private void InitializeFolders()
        {
            try
            {
                if (!Directory.Exists(OrigImageDir)) Directory.CreateDirectory(OrigImageDir);
                if (!Directory.Exists(ResultImageDir)) Directory.CreateDirectory(ResultImageDir);
                if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create folders: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== 事件 ====================

        /// <summary>
        /// Load 按钮
        /// </summary>
        private void Btn_Load_Click(object sender, RoutedEventArgs e)
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
                    _currentSavedOrigPath = "";   // 换图了，重置保存路径
                    OrigPathText.Text = "Orig Path: " + filePath;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Load picture fail: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 条码输入框：按 Enter 触发侦测（扫码枪以 Enter 结尾时同样触发）
        /// </summary>
        private void Txt_Barcode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                Btn_Detect_Click(this.Btn_Detect, new RoutedEventArgs());
            }
        }

        /// <summary>
        /// OK 按钮：执行 YOLO 侦测
        /// </summary>
        private async void Btn_Detect_Click(object sender, RoutedEventArgs e)
        {
            if (_detector == null)
            {
                MessageBox.Show("YOLO model is not loaded.", "Error");
                return;
            }

            if (string.IsNullOrEmpty(_currentPhotoPath) || !File.Exists(_currentPhotoPath))
            {
                MessageBox.Show("Please load an image first.", "Notice");
                return;
            }

            string selectedClass = cmb_DetectionClass.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selectedClass))
            {
                MessageBox.Show("Please select a detection class.", "Notice");
                return;
            }

            // 读取 Confidence 阈值
            float confThreshold = 0.25f;
            if (!float.TryParse(TxtConfidence.Text, out confThreshold))
                confThreshold = 0.25f;
            confThreshold = Math.Max(0f, Math.Min(1f, confThreshold));

            // 读取条码
            string barcode = Txt_Barcode.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(barcode))
                barcode = "NOBARCODE";

            // 过滤非法文件名字符
            foreach (var c in Path.GetInvalidFileNameChars())
                barcode = barcode.Replace(c, '_');

            try
            {
                Btn_Detect.IsEnabled = false;

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string resultPath = Path.Combine(ResultImageDir, $"result_{barcode}_{timestamp}.jpg");
                string origSavePath = Path.Combine(OrigImageDir, $"orig_{barcode}_{timestamp}.jpg");

                // 保存原图
                try
                {
                    File.Copy(_currentPhotoPath, origSavePath, true);
                    _currentSavedOrigPath = origSavePath;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save original image: {ex.Message}", "Warning",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                // 执行检测
                DetectionResult detResult = await Task.Run(() =>
                    _detector.Detect(_currentPhotoPath, resultPath, selectedClass, confThreshold));

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
                bool found = detResult.ValidDetections.Count > 0;

                // 写日志
                AddLog(found, detResult, selectedClass);
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

        /// <summary>
        /// 添加一条日志
        /// </summary>
        private void AddLog(bool found, DetectionResult detResult, string selectedClass)
        {
            string time = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
            string status = found ? "OK" : "NG";
            string detail;

            // 图片宽高
            int imgW = 0, imgH = 0;
            if (File.Exists(_currentPhotoPath))
            {
                using var mat = OpenCvSharp.Cv2.ImRead(_currentPhotoPath);
                imgW = mat.Width;
                imgH = mat.Height;
            }

            if (found)
            {
                var best = detResult.ValidDetections.OrderByDescending(d => d.Confidence).First();
                int cx = best.X + best.Width / 2;
                int cy = best.Y + best.Height / 2;

                detail = $"Class: {best.ClassName}, Conf: {best.Confidence:F2}, " +
                         $"ImgSize: {imgW}x{imgH}, Center: ({cx},{cy})";
            }
            else if (detResult.LowConfDetections.Count > 0)
            {
                var best = detResult.LowConfDetections.OrderByDescending(d => d.Confidence).First();
                int cx = best.X + best.Width / 2;
                int cy = best.Y + best.Height / 2;

                detail = $"[Confidence below threshold] Class: {best.ClassName}, Conf: {best.Confidence:F2}, " +
                         $"ImgSize: {imgW}x{imgH}, Center: ({cx},{cy})";
            }
            else
            {
                detail = "Not detected: target not found";
            }

            // 加入 DataGrid
            OperationLogs.Insert(0, new LogItem
            {
                Time = time,
                Status = status,
                Detail = detail
            });

            // 写日志文件
            try
            {
                string logFile = Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
                string line = $"{time}\t{status}\t{detail}";
                File.AppendAllText(logFile, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to write log file: {ex.Message}", "Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Photo 的 Open：优先打开保存后的原图
        /// </summary>
        private void Btn_Open_Click(object sender, RoutedEventArgs e)
        {
            string pathToOpen = !string.IsNullOrEmpty(_currentSavedOrigPath)
                ? _currentSavedOrigPath
                : _currentPhotoPath;

            OpenFolderAndSelectFile(pathToOpen);
        }

        /// <summary>
        /// Detection 的 Open
        /// </summary>
        private void Btn_OpenResult_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderAndSelectFile(_currentResultPath);
        }

        /// <summary>
        /// 打开文件夹并选中文件
        /// </summary>
        private void OpenFolderAndSelectFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                MessageBox.Show("Images have not yet loaded.", "Message",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!File.Exists(filePath))
            {
                MessageBox.Show($"File does not exist: {filePath}", "Message",
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
    }
}