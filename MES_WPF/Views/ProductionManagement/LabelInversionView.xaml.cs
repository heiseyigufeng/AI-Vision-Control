using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using OpenCvSharp;
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
        private string _currentSavedOrigPath = "";

        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string AssetsDir = Path.Combine(BaseDir, "Assets");
        private static readonly string OrigImageDir = Path.Combine(BaseDir, "OrigImage");
        private static readonly string ResultImageDir = Path.Combine(BaseDir, "ResultImage");
        private static readonly string LogDir = Path.Combine(BaseDir, "logs");

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

            // 进入页面：若相机已打开，自动预览
            Loaded += (s, e) =>
            {
                if (_cameraService.IsOpened)
                {
                    StartPreview();
                }
            };

            // 离开页面：只停定时器，不关相机
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
                    AddLog("NG", "Failed to open camera.");
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
                {
                    CameraImage.Source = bitmap;
                }
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

                string txtPath = Path.Combine(AssetsDir, "DetectionClass.txt");
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
                string filePath = Path.Combine(AssetsDir, "DetectionClass.txt");
                if (!File.Exists(filePath)) return;

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

                    // ========== 自动侦测 ==========
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
            // 检查 YOLO 模型
            if (_detector == null)
            {
                MessageBox.Show("YOLO model is not loaded.", "Error");
                return;
            }

            // 检查检测类别
            string selectedClass = cmb_DetectionClass.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selectedClass))
            {
                MessageBox.Show("Please select a detection class.", "Notice");
                return;
            }

            // 读取 Confidence
            float confThreshold = 0.25f;
            if (!float.TryParse(TxtConfidence.Text, out confThreshold))
                confThreshold = 0.25f;
            confThreshold = Math.Max(0f, Math.Min(1f, confThreshold));

            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // 结果图用 result_Load_ 开头
                string resultPath = Path.Combine(ResultImageDir, $"result_Load_{timestamp}.jpg");

                // 保存原图（保留原命名，orig_Load_）
                string origSavePath = Path.Combine(OrigImageDir, $"orig_Load_{timestamp}.jpg");
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

                // 执行检测
                DetectionResult detResult = await Task.Run(() =>
                    _detector.Detect(imagePath, resultPath, selectedClass, confThreshold));

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
                AddDetectionLog(found, detResult, selectedClass);
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
            // 1. 检查相机是否打开
            if (!_cameraService.IsOpened)
            {
                AddLog("NG", "相机未打开");
                return;
            }

            // 2. 检查 YOLO 模型
            if (_detector == null)
            {
                MessageBox.Show("YOLO model is not loaded.", "Error");
                return;
            }

            // 3. 检查检测类别
            string selectedClass = cmb_DetectionClass.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selectedClass))
            {
                MessageBox.Show("Please select a detection class.", "Notice");
                return;
            }

            // 4. 抓取相机当前帧
            if (_lastCameraFrame == null || _lastCameraFrame.Empty())
            {
                AddLog("NG", "相机未打开");
                return;
            }

            // 5. 读取 Confidence
            float confThreshold = 0.25f;
            if (!float.TryParse(TxtConfidence.Text, out confThreshold))
                confThreshold = 0.25f;
            confThreshold = Math.Max(0f, Math.Min(1f, confThreshold));

            // 6. 读取条码
            string barcode = Txt_Barcode.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(barcode))
                barcode = "NOBARCODE";
            foreach (var c in Path.GetInvalidFileNameChars())
                barcode = barcode.Replace(c, '_');

            try
            {
                Btn_Detect.IsEnabled = false;

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string resultPath = Path.Combine(ResultImageDir, $"result_{barcode}_{timestamp}.jpg");
                string origSavePath = Path.Combine(OrigImageDir, $"orig_{barcode}_{timestamp}.jpg");

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

                // 9. 用相机帧做 YOLO 检测
                DetectionResult detResult = await Task.Run(() =>
                    _detector.Detect(origSavePath, resultPath, selectedClass, confThreshold));

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

                // 11. 判断 OK / NG
                bool found = detResult.ValidDetections.Count > 0;

                AddDetectionLog(found, detResult, selectedClass);
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

        // ==================== 日志 ====================

        // ==================== 日志 ====================

        /// <summary>
        /// 添加一条操作日志（通用）
        /// </summary>
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
                string logFile = Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(logFile, $"{time}\t{status}\t{detail}{Environment.NewLine}");
            }
            catch { }
        }

        /// <summary>
        /// 添加一条检测日志（包含检测信息）
        /// </summary>
        private void AddDetectionLog(bool found, DetectionResult detResult, string selectedClass)
        {
            string status = found ? "OK" : "NG";
            string detail;

            int imgW = 0, imgH = 0;
            if (File.Exists(_currentPhotoPath))
            {
                using var mat = Cv2.ImRead(_currentPhotoPath);
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
    }
}