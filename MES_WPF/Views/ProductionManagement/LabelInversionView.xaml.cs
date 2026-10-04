using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        private string _currentPhotoPath = "";
        private string _currentResultPath = "";

        // 程序所在目录 + 两个子文件夹
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string OrigImageDir = Path.Combine(BaseDir, "OrigImage");
        private static readonly string ResultImageDir = Path.Combine(BaseDir, "ResultImage");
        private static readonly string LogDir = Path.Combine(BaseDir, "logs");

        // ========== YOLO 相关 ==========
        private static readonly string ModelPath = Path.Combine(BaseDir, "Assets", "family.onnx");
        private YoloOnnxDetector _detector;

        // ========== 操作日志 ==========
        public ObservableCollection<LogItem> OperationLogs { get; } = new ObservableCollection<LogItem>();

        public LabelInversionView()
        {
            InitializeComponent();

            // 让 XAML 里的 DataGrid 能找到 OperationLogs
            DataContext = this;

            InitializeFolders();
            LoadDetectionClasses();
            InitializeYolo();
        }

        /// <summary>
        /// 日志项
        /// </summary>
        public class LogItem
        {
            public string Time { get; set; }
            public string Status { get; set; }
            public string Detail { get; set; }
        }

        /// <summary>
        /// 初始化 YOLO
        /// </summary>
        private void InitializeYolo()
        {
            try
            {
                if (!File.Exists(ModelPath))
                {
                    MessageBox.Show($"ONNX model not found: {ModelPath}", "Error",
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

                _detector = new YoloOnnxDetector(ModelPath, classNames);
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
                string filePath = Path.Combine(BaseDir, "Assets", "DetectionClass.txt");

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
            {
                confThreshold = 0.25f;
            }
            confThreshold = Math.Max(0f, Math.Min(1f, confThreshold));

            try
            {
                Btn_Detect.IsEnabled = false;

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string resultPath = Path.Combine(ResultImageDir, $"result_{timestamp}.jpg");

                // 后台执行检测
                DetectionResult detResult = await Task.Run(() =>
                    _detector.Detect(_currentPhotoPath, resultPath, selectedClass, confThreshold));

                // ========== 显示结果图 ==========
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

                // ========== 判断 OK / NG ==========
                bool found = detResult.Detections.Count > 0;

                // 注意：这里不再修改 Btn_Detect 的 Content / Background / Foreground / BorderBrush

                // ========== 写日志 ==========
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
        /// 添加一条日志（DataGrid + 文件）
        /// </summary>
        private void AddLog(bool found, DetectionResult detResult, string selectedClass)
        {
            string time = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
            string status = found ? "OK" : "NG";
            string detail;

            if (found)
            {
                // 取置信度最高的那个
                var best = detResult.Detections.OrderByDescending(d => d.Confidence).First();

                // 图片宽高（从 Detections 拿不到，从原图读一次，或者从记录里拿）
                int imgW = 0, imgH = 0;
                if (File.Exists(_currentPhotoPath))
                {
                    using var mat = OpenCvSharp.Cv2.ImRead(_currentPhotoPath);
                    imgW = mat.Width;
                    imgH = mat.Height;
                }

                // 框中心点
                int cx = best.X + best.Width / 2;
                int cy = best.Y + best.Height / 2;

                detail = $"Class: {best.ClassName}, Conf: {best.Confidence:F2}, " +
                         $"ImgSize: {imgW}x{imgH}, Center: ({cx},{cy})";
            }
            else
            {
                detail = "Not detected: target not found";
            }

            // 1. 加入 DataGrid
            OperationLogs.Insert(0, new LogItem
            {
                Time = time,
                Status = status,
                Detail = detail
            });

            // 2. 写入按天分文件的日志
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
        /// Photo 的 Open
        /// </summary>
        private void Btn_Open_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderAndSelectFile(_currentPhotoPath);
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