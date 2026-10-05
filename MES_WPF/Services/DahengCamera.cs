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
        // 最后一次失败原因
        public string LastError { get; private set; } = "";

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
            LastError = "";

            // 1. 初始化 SDK
            try
            {
                _factory = IGXFactory.GetInstance();
                _factory.Init();
            }
            catch (Exception ex)
            {
                LastError = $"Init SDK failed: {ex.Message}";
                return false;
            }

            // 2. 枚举设备
            var deviceList = new List<IGXDeviceInfo>();
            try
            {
                _factory.UpdateAllDeviceList(200, deviceList);
            }
            catch (Exception ex)
            {
                LastError = $"UpdateDeviceList failed: {ex.Message}";
                Close();
                return false;
            }

            if (deviceList.Count == 0)
            {
                LastError = "No Daheng device found (check cable/power/IP)";
                Close();
                return false;
            }

            // 3. 打开设备
            try
            {
                _device = _factory.OpenDeviceBySN(deviceList[0].GetSN(),
                    GX_ACCESS_MODE.GX_ACCESS_EXCLUSIVE);
                _featureControl = _device.GetRemoteFeatureControl();
            }
            catch (Exception ex)
            {
                LastError = $"OpenDeviceBySN failed: {ex.Message}";
                Close();
                return false;
            }

            // 4. 判断彩色/黑白
            try
            {
                DetectIsColor();
            }
            catch (Exception ex)
            {
                LastError = $"DetectIsColor failed: {ex.Message}";
                Close();
                return false;
            }

            // 5. 读取宽高
            try
            {
                _width = (int)_featureControl.GetIntFeature("Width").GetValue();
                _height = (int)_featureControl.GetIntFeature("Height").GetValue();
            }
            catch (Exception ex)
            {
                LastError = $"Read Width/Height failed: {ex.Message}";
                Close();
                return false;
            }

            // 6. 打开流
            try
            {
                _stream = _device.OpenStream(0);
            }
            catch (Exception ex)
            {
                LastError = $"OpenStream failed: {ex.Message}";
                Close();
                return false;
            }

            // 7. 设置连续采集模式
            try
            {
                _featureControl.GetEnumFeature("AcquisitionMode").SetValue("Continuous");
            }
            catch (Exception ex)
            {
                LastError = $"Set AcquisitionMode failed: {ex.Message}";
                Close();
                return false;
            }

            // 8. 网络相机设置最优包大小
            try
            {
                var deviceClass = _device.GetDeviceInfo().GetDeviceClass();
                if (deviceClass == GX_DEVICE_CLASS_LIST.GX_DEVICE_CLASS_GEV)
                {
                    if (_featureControl.IsImplemented("GevSCPSPacketSize"))
                    {
                        uint packetSize = _stream.GetOptimalPacketSize();
                        _featureControl.GetIntFeature("GevSCPSPacketSize").SetValue(packetSize);
                    }
                }
            }
            catch (Exception ex)
            {
                // 包大小失败不致命，只记录警告
                LastError = $"Set packet size warning: {ex.Message}";
            }

            // 9. 创建格式转换器
            try
            {
                _formatConvert = _factory.CreateImageFormatConvert();
                _formatConvert.SetDstFormat(GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BGR8);
            }
            catch (Exception ex)
            {
                LastError = $"Create ImageFormatConvert failed: {ex.Message}";
                Close();
                return false;
            }

            // 10. 分配 buffer
            try
            {
                _outBufferSize = _width * _height * 3;
                _outBuffer = Marshal.AllocCoTaskMem(_outBufferSize);
            }
            catch (Exception ex)
            {
                LastError = $"Alloc buffer failed: {ex.Message}";
                Close();
                return false;
            }

            // 11. 注册回调 + 开始采集
            try
            {
                _stream.RegisterCaptureCallback(this, OnFrameReceived);
                _stream.StartGrab();
                _featureControl.GetCommandFeature("AcquisitionStart").Execute();
            }
            catch (Exception ex)
            {
                LastError = $"StartGrab failed: {ex.Message}";
                Close();
                return false;
            }

            IsOpened = true;
            return true;
        }

        /// <summary>
        /// 判断是否彩色相机
        /// </summary>
        private void DetectIsColor()
        {
            try
            {
                string pixelFormat = _featureControl.GetEnumFeature("PixelFormat").GetValue();
                _isColor = !pixelFormat.StartsWith("Mono", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                _isColor = true;
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

                // 格式转换 BGR8（true = 上下翻转）
                ulong dstSize = _formatConvert.GetBufferSizeForConversion(frameData);
                _formatConvert.Convert(frameData, _outBuffer, dstSize, false);

                // 拷贝到 byte[]
                int stride = _width * 3;
                byte[] bgrBytes = new byte[stride * _height];
                Marshal.Copy(_outBuffer, bgrBytes, 0, bgrBytes.Length);

                // byte[] → Mat
                var mat = new Mat(_height, _width, MatType.CV_8UC3);
                Marshal.Copy(bgrBytes, 0, mat.Data, bgrBytes.Length);

                // 水平翻转（修正左右颠倒）
                Cv2.Flip(mat, mat, FlipMode.Y);

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