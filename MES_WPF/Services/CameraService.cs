using OpenCvSharp;
using System;

namespace MES_WPF.Services
{
    /// <summary>
    /// 相机类型
    /// </summary>
    public enum CameraType
    {
        None,
        LaptopCam
    }

    /// <summary>
    /// 相机服务：当前用笔记本自带摄像头（OpenCV VideoCapture）
    /// </summary>
    public class CameraService : IDisposable
    {
        public CameraType CurrentType { get; private set; } = CameraType.None;
        public bool IsOpened => CurrentType != CameraType.None;

        private VideoCapture _capture;

        /// <summary>
        /// 打开相机
        /// </summary>
        /// <param name="cameraIndex">摄像头索引（0 是默认摄像头）</param>
        /// <returns>是否成功</returns>
        public bool Open(int cameraIndex = 0)
        {
            try
            {
                // 先关闭已打开的
                Close();

                _capture = new VideoCapture(cameraIndex);
                if (!_capture.IsOpened())
                {
                    _capture?.Dispose();
                    _capture = null;
                    CurrentType = CameraType.None;
                    return false;
                }

                // 设置分辨率
                _capture.Set(VideoCaptureProperties.FrameWidth, 1280);
                _capture.Set(VideoCaptureProperties.FrameHeight, 720);

                CurrentType = CameraType.LaptopCam;
                return true;
            }
            catch
            {
                CurrentType = CameraType.None;
                return false;
            }
        }

        /// <summary>
        /// 抓一帧，返回 Mat（BGR）。失败返回 null
        /// </summary>
        public Mat GrabFrame()
        {
            try
            {
                if (_capture == null || !_capture.IsOpened())
                    return null;

                var frame = new Mat();
                if (!_capture.Read(frame) || frame.Empty())
                {
                    frame.Dispose();
                    return null;
                }

                return frame;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 关闭相机
        /// </summary>
        public void Close()
        {
            try
            {
                if (_capture != null)
                {
                    _capture.Release();
                    _capture.Dispose();
                    _capture = null;
                }
            }
            catch { }

            CurrentType = CameraType.None;
        }

        public void Dispose()
        {
            Close();
        }
    }
}