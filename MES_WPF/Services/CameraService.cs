using OpenCvSharp;
using System;

namespace MES_WPF.Services
{
    public enum CameraType
    {
        None,
        Daheng,
        LaptopCam
    }

    public class CameraService : IDisposable
    {
        public CameraType CurrentType { get; private set; } = CameraType.None;
        public bool IsOpened => CurrentType != CameraType.None;

        private DahengCamera _dahengCamera;
        private VideoCapture _laptopCapture;

        /// <summary>
        /// 打开相机：优先大恒，失败再用笔记本
        /// </summary>
        public bool Open()
        {
            // 1. 先试大恒
            try
            {
                _dahengCamera = new DahengCamera();
                if (_dahengCamera.Open())
                {
                    CurrentType = CameraType.Daheng;
                    return true;
                }
                _dahengCamera = null;
            }
            catch
            {
                _dahengCamera = null;
            }

            // 2. 再试笔记本摄像头
            try
            {
                _laptopCapture = new VideoCapture(0);
                if (_laptopCapture.IsOpened())
                {
                    _laptopCapture.Set(VideoCaptureProperties.FrameWidth, 1280);
                    _laptopCapture.Set(VideoCaptureProperties.FrameHeight, 720);
                    CurrentType = CameraType.LaptopCam;
                    return true;
                }
                _laptopCapture?.Dispose();
                _laptopCapture = null;
            }
            catch
            {
                _laptopCapture = null;
            }

            // 3. 都没找到
            CurrentType = CameraType.None;
            return false;
        }

        /// <summary>
        /// 抓一帧，返回 Mat（BGR）
        /// </summary>
        public Mat GrabFrame()
        {
            try
            {
                switch (CurrentType)
                {
                    case CameraType.Daheng:
                        return _dahengCamera?.GrabFrame();

                    case CameraType.LaptopCam:
                        if (_laptopCapture == null || !_laptopCapture.IsOpened()) return null;
                        var frame = new Mat();
                        if (!_laptopCapture.Read(frame) || frame.Empty())
                        {
                            frame.Dispose();
                            return null;
                        }
                        return frame;

                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        public void Close()
        {
            try
            {
                _dahengCamera?.Close();
                _dahengCamera = null;
            }
            catch { }

            try
            {
                _laptopCapture?.Release();
                _laptopCapture?.Dispose();
                _laptopCapture = null;
            }
            catch { }

            CurrentType = CameraType.None;
        }

        public void Dispose() => Close();
    }
}