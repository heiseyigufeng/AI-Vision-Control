using GxIAPINET;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MES_WPF.Services
{
    /// <summary>
    /// 大恒相机封装（返回 OpenCV Mat）
    /// </summary>
    public class DahengCamera : IDisposable
    {
        private IGXFactory _factory;
        private IGXDevice _device;
        private IGXStream _stream;
        private IGXFeatureControl _featureControl;
        private IGXImageFormatConvert _formatConvert;

        // 图像尺寸
        private int _width;
        private int _height;
        private bool _isColor;

        // 最新一帧缓存
        private Mat _latestFrame;
        private readonly object _lock = new object();

        // 输出 buffer（BGR8）
        private IntPtr _outBuffer = IntPtr.Zero;
        private int _outBufferSize = 0;

        public bool IsOpened { get; private set; } = false;

        /// <summary>
        /// 打开第一台大恒相机
        /// </summary>
        public bool Open()
        {
            try
            {
                _factory = IGXFactory.GetInstance();
                _factory.Init();

                var deviceList = new List<IGXDeviceInfo>();
                _factory.UpdateAllDeviceList(200, deviceList);

                if (deviceList.Count == 0)
                {
                    Close();
                    return false;
                }

                // 打开第一台
                _device = _factory.OpenDeviceBySN(deviceList[0].GetSN(),
                    GX_ACCESS_MODE.GX_ACCESS_EXCLUSIVE);
                _featureControl = _device.GetRemoteFeatureControl();

                // 判断彩色/黑白
                DetectIsColor();

                // 读取宽高
                _width = (int)_featureControl.GetIntFeature("Width").GetValue();
                _height = (int)_featureControl.GetIntFeature("Height").GetValue();

                // 打开流
                _stream = _device.OpenStream(0);

                // 连续采集模式
                _featureControl.GetEnumFeature("AcquisitionMode").SetValue("Continuous");

                // 网络相机设置最优包大小
                var deviceClass = _device.GetDeviceInfo().GetDeviceClass();
                if (deviceClass == GX_DEVICE_CLASS_LIST.GX_DEVICE_CLASS_GEV)
                {
                    if (_featureControl.IsImplemented("GevSCPSPacketSize"))
                    {
                        uint packetSize = _stream.GetOptimalPacketSize();
                        _featureControl.GetIntFeature("GevSCPSPacketSize").SetValue(packetSize);
                    }
                }

                // 创建格式转换器
                _formatConvert = _factory.CreateImageFormatConvert();
                _formatConvert.SetDstFormat(GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BGR8);

                // 分配输出 buffer
                _outBufferSize = _width * _height * 3;
                _outBuffer = Marshal.AllocCoTaskMem(_outBufferSize);

                // 注册回调 + 开始采集
                _stream.RegisterCaptureCallback(this, OnFrameReceived);
                _stream.StartGrab();
                _featureControl.GetCommandFeature("AcquisitionStart").Execute();

                IsOpened = true;
                return true;
            }
            catch
            {
                Close();
                return false;
            }
        }

        /// <summary>
        /// 判断是否彩色相机
        /// </summary>
        private void DetectIsColor()
        {
            try
            {
                string pixelFormat = _featureControl.GetEnumFeature("PixelFormat").GetValue();
                // 如果 PixelFormat 以 "Mono" 开头，则是黑白
                _isColor = !pixelFormat.StartsWith("Mono", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                _isColor = true;   // 默认当作彩色
            }
        }

        /// <summary>
        /// 大恒回调：每帧到达
        /// </summary>
        private void OnFrameReceived(object userParam, IFrameData frameData)
        {
            try
            {
                if (frameData == null) return;
                if (frameData.GetStatus() != GX_FRAME_STATUS_LIST.GX_FRAME_STATUS_SUCCESS)
                    return;

                // 格式转换 BGR8
                ulong dstSize = _formatConvert.GetBufferSizeForConversion(frameData);
                _formatConvert.Convert(frameData, _outBuffer, dstSize, true);

                // 拷贝到 byte[]
                int stride = _width * 3;
                byte[] bgrBytes = new byte[stride * _height];
                Marshal.Copy(_outBuffer, bgrBytes, 0, bgrBytes.Length);

                // 大恒图像是上下翻转的，需要翻回来
                byte[] flipped = new byte[bgrBytes.Length];
                for (int i = 0; i < _height; i++)
                {
                    Buffer.BlockCopy(bgrBytes,
                        (_height - i - 1) * stride,
                        flipped,
                        i * stride,
                        stride);
                }

                // byte[] → Mat
                var mat = new Mat(_height, _width, MatType.CV_8UC3);
                Marshal.Copy(flipped, 0, mat.Data, flipped.Length);

                // ========== 水平翻转（修正左右颠倒） ==========
                Cv2.Flip(mat, mat, FlipMode.Y);
                // ============================================

                lock (_lock)
                {
                    _latestFrame?.Dispose();
                    _latestFrame = mat;
                }
            }
            catch
            {
                // 忽略回调异常
            }
        }

        /// <summary>
        /// 取最近一帧（返回克隆，外部负责释放）
        /// </summary>
        public Mat GrabFrame()
        {
            lock (_lock)
            {
                return _latestFrame?.Clone();
            }
        }

        /// <summary>
        /// 关闭相机
        /// </summary>
        public void Close()
        {
            try
            {
                if (_featureControl != null)
                {
                    try { _featureControl.GetCommandFeature("AcquisitionStop").Execute(); }
                    catch { }
                }

                if (_stream != null)
                {
                    try { _stream.StopGrab(); } catch { }
                    try { _stream.UnregisterCaptureCallback(); } catch { }
                    try { _stream.Close(); } catch { }
                    _stream = null;
                }

                if (_device != null)
                {
                    try { _device.Close(); } catch { }
                    _device = null;
                }

                if (_factory != null)
                {
                    try { _factory.Uninit(); } catch { }
                    _factory = null;
                }

                if (_outBuffer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(_outBuffer);
                    _outBuffer = IntPtr.Zero;
                }

                _formatConvert = null;
            }
            catch { }

            lock (_lock)
            {
                _latestFrame?.Dispose();
                _latestFrame = null;
            }

            IsOpened = false;
        }

        public void Dispose() => Close();
    }
}