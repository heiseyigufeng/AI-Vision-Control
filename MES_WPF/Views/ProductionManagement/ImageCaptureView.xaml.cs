using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenCvSharp;
using MES_WPF.Services;

namespace MES_WPF.Views.ProductionManagement
{
    /// <summary>
    /// ImageCaptureView.xaml 的交互逻辑
    /// </summary>
    public partial class ImageCaptureView : UserControl
    {
        // ========== 相机服务（DI 单例注入） ==========
        private readonly CameraService _cameraService;
        private System.Windows.Threading.DispatcherTimer _previewTimer;

        // ========== 保存文件夹 ==========
        private string _saveFolder = "";

        // ========== 序号（当天递增） ==========
        private int _sequenceNumber = 0;
        private DateTime _lastSequenceDate = DateTime.MinValue;

        // ========== 操作日志 ==========
        public ObservableCollection<LogItem> OperationLogs { get; } = new ObservableCollection<LogItem>();

        // ========== 当前帧缓存 ==========
        private Mat _lastFrame;

        public ImageCaptureView(CameraService cameraService)
        {
            InitializeComponent();

            // DI 注入的相机服务（单例）
            _cameraService = cameraService ?? new CameraService();

            DataContext = this;

            // 进入页面：如果相机已打开，自动启动预览
            Loaded += (s, e) =>
            {
                if (_cameraService.IsOpened)
                {
                    StartPreview();
                }
            };

            // 离开页面：只停定时器，不关相机（单例，由 App.OnExit 统一关闭）
            Unloaded += (s, e) =>
            {
                StopPreview();
                _lastFrame?.Dispose();
                _lastFrame = null;
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

        /// <summary>
        /// Open Camera 按钮
        /// </summary>
        private void Btn_OpenCamera_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_cameraService.IsOpened)
                {
                    AddLog("OK", "Camera already opened.");
                    return;
                }

                bool ok = _cameraService.Open();
                if (!ok)
                {
                    AddLog("NG", "Failed to open camera: no camera found.");
                    return;
                }

                StartPreview();
                AddLog("OK", $"Camera opened. Type: {_cameraService.CurrentType}");
            }
            catch (Exception ex)
            {
                AddLog("NG", $"Open camera exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Close Camera 按钮
        /// </summary>
        private void Btn_CloseCamera_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_cameraService.IsOpened)
                {
                    AddLog("NG", "Camera is not opened.");
                    return;
                }

                StopPreview();
                _cameraService.Close();

                // 清空画面
                CameraImage.Source = null;
                CameraPlaceholder.Visibility = Visibility.Visible;

                AddLog("OK", "Camera closed.");
            }
            catch (Exception ex)
            {
                AddLog("NG", $"Close camera exception: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动预览定时器
        /// </summary>
        private void StartPreview()
        {
            CameraPlaceholder.Visibility = Visibility.Collapsed;

            // 避免重复启动
            if (_previewTimer != null) return;

            _previewTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)   // 约 20fps
            };
            _previewTimer.Tick += (s, e) => UpdatePreview();
            _previewTimer.Start();
        }

        /// <summary>
        /// 停止预览定时器
        /// </summary>
        private void StopPreview()
        {
            if (_previewTimer != null)
            {
                _previewTimer.Stop();
                _previewTimer = null;
            }
        }

        /// <summary>
        /// 刷新相机预览
        /// </summary>
        private void UpdatePreview()
        {
            try
            {
                var frame = _cameraService.GrabFrame();
                if (frame == null) return;

                // 缓存当前帧
                _lastFrame?.Dispose();
                _lastFrame = frame;

                // Mat → BitmapImage
                var bitmap = MatToBitmapSource(frame);
                if (bitmap != null)
                {
                    CameraImage.Source = bitmap;
                }
            }
            catch
            {
                // 忽略预览错误
            }
        }

        // ==================== 保存文件夹 ====================

        /// <summary>
        /// Select Folder 按钮
        /// </summary>
        private void Btn_SelectFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Select Save Folder"
                };

                if (dialog.ShowDialog() == true)
                {
                    _saveFolder = dialog.FolderName;
                    AddLog("OK", $"Folder selected: {_saveFolder}");
                }
                else
                {
                    AddLog("NG", "Folder selection cancelled.");
                }
            }
            catch (Exception ex)
            {
                AddLog("NG", $"Select folder exception: {ex.Message}");
            }
        }

        // ==================== 拍照 ====================

        /// <summary>
        /// Take a Picture 按钮
        /// </summary>
        private void Btn_TakePicture_Click(object sender, RoutedEventArgs e)
        {
            // 检查 1：文件夹是否已选
            if (string.IsNullOrEmpty(_saveFolder) || !Directory.Exists(_saveFolder))
            {
                AddLog("NG", "Please select a save folder first.");
                return;
            }

            // 检查 2：相机是否打开
            if (!_cameraService.IsOpened)
            {
                AddLog("NG", "Camera is not opened.");
                return;
            }

            // 检查 3：是否有帧
            if (_lastFrame == null || _lastFrame.Empty())
            {
                AddLog("NG", "No frame captured.");
                return;
            }

            // 检查 4：是否是纯色画面（无图）
            if (IsSolidColor(_lastFrame))
            {
                AddLog("NG", "Camera image is blank (solid color).");
                return;
            }

            try
            {
                // 生成文件名：yyyyMMdd_HHmmss_NNNN.jpg
                string fileName = GenerateFileName();
                string fullPath = Path.Combine(_saveFolder, fileName);

                // 保存
                Cv2.ImWrite(fullPath, _lastFrame);

                // 显示到右侧
                var bitmap = MatToBitmapSource(_lastFrame);
                if (bitmap != null)
                {
                    PhotoImage.Source = bitmap;
                    PhotoImage.Visibility = Visibility.Visible;
                    PhotoPlaceholder.Visibility = Visibility.Collapsed;
                }

                // 更新路径文字
                PhotoPathText.Text = "Photo: " + fullPath;

                AddLog("OK", $"Saved: {fullPath}");
            }
            catch (Exception ex)
            {
                AddLog("NG", $"Take picture exception: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成文件名：yyyyMMdd_HHmmss_NNNN.jpg
        /// 序号按天重置，从 0001 开始
        /// </summary>
        private string GenerateFileName()
        {
            DateTime now = DateTime.Now;

            // 跨天，重置序号
            if (now.Date != _lastSequenceDate.Date)
            {
                _sequenceNumber = 0;
                _lastSequenceDate = now;
            }

            _sequenceNumber++;

            return $"{now:yyyyMMdd_HHmmss}_{_sequenceNumber:D4}.jpg";
        }

        /// <summary>
        /// 判断画面是否为纯色（无图/黑屏/白屏）
        /// </summary>
        private bool IsSolidColor(Mat frame)
        {
            try
            {
                // 采样：缩到很小，看标准差
                using var small = new Mat();
                Cv2.Resize(frame, small, new OpenCvSharp.Size(64, 64));

                var mean = new Scalar();
                var stddev = new Scalar();
                Cv2.MeanStdDev(small, out mean, out stddev);

                // 如果标准差很低，说明颜色几乎一致
                double avgStd = (stddev.Val0 + stddev.Val1 + stddev.Val2) / 3.0;
                return avgStd < 3.0;
            }
            catch
            {
                return false;
            }
        }

        // ==================== 工具方法 ====================

        /// <summary>
        /// OpenCV Mat → WPF BitmapSource
        /// </summary>
        private System.Windows.Media.Imaging.BitmapSource MatToBitmapSource(Mat mat)
        {
            try
            {
                // Mat 是 BGR，转 BGRA
                using var bgra = new Mat();
                Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);

                int width = bgra.Width;
                int height = bgra.Height;
                int stride = (int)bgra.Step();
                byte[] data = new byte[stride * height];

                System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);

                return System.Windows.Media.Imaging.BitmapSource.Create(
                    width, height, 96, 96,
                    PixelFormats.Bgra32,
                    null, data, stride);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 添加一条操作日志
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
        }
    }
}