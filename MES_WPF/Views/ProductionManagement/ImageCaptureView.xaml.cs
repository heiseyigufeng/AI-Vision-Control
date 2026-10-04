using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private string _lastSavedFilePath = "";

        // ========== 序号（当天递增） ==========
        private int _sequenceNumber = 0;
        private DateTime _lastSequenceDate = DateTime.MinValue;

        // ========== 操作日志 ==========
        public ObservableCollection<LogItem> OperationLogs { get; } = new ObservableCollection<LogItem>();

        // ========== 当前帧缓存 ==========
        private Mat _lastFrame;

        // ========== 屏幕采集模式 ==========
        private bool _isScreenMode = false;

        // ==================== P/Invoke（纯 WPF 抓屏） ====================

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(
            IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
            IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);

        private const int SRCCOPY = 0x00CC0020;

        // ==================== 构造函数 ====================

        public ImageCaptureView(CameraService cameraService)
        {
            InitializeComponent();

            _cameraService = cameraService ?? new CameraService();

            DataContext = this;

            // 进入页面
            Loaded += (s, e) =>
            {
                if (_cameraService.IsOpened && !_isScreenMode)
                {
                    StartPreview();
                }

                Focusable = true;
                Focus();
            };

            // 离开页面
            Unloaded += (s, e) =>
            {
                StopPreview();
                _lastFrame?.Dispose();
                _lastFrame = null;
            };

            // 键盘：空格键拍照
            KeyDown += ImageCaptureView_KeyDown;
        }

        // ==================== 日志项 ====================

        public class LogItem
        {
            public string Time { get; set; }
            public string Status { get; set; }
            public string Detail { get; set; }
        }

        // ==================== 键盘 ====================

        private void ImageCaptureView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                e.Handled = true;
                Btn_TakePicture_Click(this, new RoutedEventArgs());
            }
        }

        // ==================== 相机控制 ====================

        private void Btn_OpenCamera_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isScreenMode = false;

                if (_cameraService.IsOpened)
                {
                    AddLog("OK", "Camera already opened.");
                    StartPreview();
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

        private void Btn_CloseCamera_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_cameraService.IsOpened && !_isScreenMode)
                {
                    AddLog("NG", "Camera is not opened.");
                    return;
                }

                StopPreview();
                _cameraService.Close();
                _isScreenMode = false;

                CameraImage.Source = null;
                CameraPlaceholder.Visibility = Visibility.Visible;
                CameraPlaceholder.Text = "Real-time camera";

                AddLog("OK", "Camera closed.");
            }
            catch (Exception ex)
            {
                AddLog("NG", $"Close camera exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Switch Screen：切换为当前屏幕采集
        /// </summary>
        private void Btn_SwitchScreen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isScreenMode = true;

                if (_cameraService.IsOpened)
                {
                    _cameraService.Close();
                }

                CameraPlaceholder.Visibility = Visibility.Collapsed;
                StartPreview();

                AddLog("OK", "Switched to screen capture mode.");
            }
            catch (Exception ex)
            {
                AddLog("NG", $"Switch screen exception: {ex.Message}");
            }
        }

        // ==================== 预览控制 ====================

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
                Mat frame;

                if (_isScreenMode)
                {
                    frame = CaptureScreenMat();
                }
                else
                {
                    frame = _cameraService.GrabFrame();
                }

                if (frame == null) return;

                _lastFrame?.Dispose();
                _lastFrame = frame;

                var bitmap = MatToBitmapSource(frame);
                if (bitmap != null)
                {
                    CameraImage.Source = bitmap;
                }
            }
            catch
            {
                // 忽略
            }
        }

        // ==================== 屏幕采集（纯 WPF） ====================

        /// <summary>
        /// 抓取主屏幕，返回 OpenCV Mat（纯 WPF，无 WindowsForms 依赖）
        /// </summary>
        private Mat CaptureScreenMat()
        {
            try
            {
                int width = (int)SystemParameters.PrimaryScreenWidth;
                int height = (int)SystemParameters.PrimaryScreenHeight;

                if (width <= 0 || height <= 0) return null;

                IntPtr desktopWnd = GetDesktopWindow();
                IntPtr desktopDC = GetWindowDC(desktopWnd);
                if (desktopDC == IntPtr.Zero) return null;

                IntPtr memDC = CreateCompatibleDC(desktopDC);
                IntPtr hBitmap = CreateCompatibleBitmap(desktopDC, width, height);
                IntPtr oldBitmap = SelectObject(memDC, hBitmap);

                bool ok = BitBlt(memDC, 0, 0, width, height, desktopDC, 0, 0, SRCCOPY);

                Mat mat = null;
                if (ok)
                {
                    var bmpSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

                    mat = BitmapSourceToMat(bmpSource);
                }

                SelectObject(memDC, oldBitmap);
                DeleteObject(hBitmap);
                DeleteDC(memDC);
                ReleaseDC(desktopWnd, desktopDC);

                return mat;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// WPF BitmapSource → OpenCV Mat
        /// </summary>
        private Mat BitmapSourceToMat(System.Windows.Media.Imaging.BitmapSource bmpSource)
        {
            try
            {
                var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    bmpSource,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null,
                    0);

                int width = converted.PixelWidth;
                int height = converted.PixelHeight;
                int stride = width * 4;
                byte[] pixels = new byte[stride * height];

                converted.CopyPixels(pixels, stride, 0);

                using var matBgra = new Mat(height, width, MatType.CV_8UC4);
                Marshal.Copy(pixels, 0, matBgra.Data, pixels.Length);

                var matBgr = new Mat();
                Cv2.CvtColor(matBgra, matBgr, ColorConversionCodes.BGRA2BGR);

                return matBgr;
            }
            catch
            {
                return null;
            }
        }

        // ==================== 保存文件夹 ====================

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

        /// <summary>
        /// Open Folder：打开最后保存图片的文件夹
        /// </summary>
        private void Btn_OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_lastSavedFilePath) && File.Exists(_lastSavedFilePath))
                {
                    Process.Start("explorer.exe", $"/select,\"{_lastSavedFilePath}\"");
                    AddLog("OK", $"Opened folder and selected: {_lastSavedFilePath}");
                }
                else if (!string.IsNullOrEmpty(_saveFolder) && Directory.Exists(_saveFolder))
                {
                    Process.Start("explorer.exe", _saveFolder);
                    AddLog("OK", $"Opened folder: {_saveFolder}");
                }
                else
                {
                    AddLog("NG", "No saved folder yet. Please take a picture first.");
                }
            }
            catch (Exception ex)
            {
                AddLog("NG", $"Open folder exception: {ex.Message}");
            }
        }

        // ==================== 拍照 ====================

        private void Btn_TakePicture_Click(object sender, RoutedEventArgs e)
        {
            // 检查 1：文件夹
            if (string.IsNullOrEmpty(_saveFolder) || !Directory.Exists(_saveFolder))
            {
                AddLog("NG", "Please select a save folder first.");
                return;
            }

            // 检查 2：数据源
            if (!_isScreenMode && !_cameraService.IsOpened)
            {
                AddLog("NG", "Camera is not opened.");
                return;
            }

            // 检查 3：帧
            if (_lastFrame == null || _lastFrame.Empty())
            {
                AddLog("NG", "No frame captured.");
                return;
            }

            // 检查 4：纯色
            if (IsSolidColor(_lastFrame))
            {
                AddLog("NG", "Camera image is blank (solid color).");
                return;
            }

            try
            {
                string fileName = GenerateFileName();
                string fullPath = Path.Combine(_saveFolder, fileName);

                Cv2.ImWrite(fullPath, _lastFrame);

                _lastSavedFilePath = fullPath;

                var bitmap = MatToBitmapSource(_lastFrame);
                if (bitmap != null)
                {
                    PhotoImage.Source = bitmap;
                    PhotoImage.Visibility = Visibility.Visible;
                    PhotoPlaceholder.Visibility = Visibility.Collapsed;
                }

                PhotoPathText.Text = "Photo: " + fullPath;

                AddLog("OK", $"Saved: {fullPath}");
            }
            catch (Exception ex)
            {
                AddLog("NG", $"Take picture exception: {ex.Message}");
            }
        }

        private string GenerateFileName()
        {
            DateTime now = DateTime.Now;

            if (now.Date != _lastSequenceDate.Date)
            {
                _sequenceNumber = 0;
                _lastSequenceDate = now;
            }

            _sequenceNumber++;

            return $"{now:yyyyMMdd_HHmmss}_{_sequenceNumber:D4}.jpg";
        }

        private bool IsSolidColor(Mat frame)
        {
            try
            {
                using var small = new Mat();
                Cv2.Resize(frame, small, new OpenCvSharp.Size(64, 64));

                var mean = new Scalar();
                var stddev = new Scalar();
                Cv2.MeanStdDev(small, out mean, out stddev);

                double avgStd = (stddev.Val0 + stddev.Val1 + stddev.Val2) / 3.0;
                return avgStd < 3.0;
            }
            catch
            {
                return false;
            }
        }

        // ==================== 工具 ====================

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

                Marshal.Copy(bgra.Data, data, 0, data.Length);

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