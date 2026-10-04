using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MES_WPF.Services
{
    /// <summary>
    /// YOLO ONNX 检测器
    /// </summary>
    public class YoloOnnxDetector : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string[] _classNames;
        private readonly int _inputWidth = 640;
        private readonly int _inputHeight = 640;
        private readonly float _confThreshold = 0.25f;
        private readonly float _iouThreshold = 0.45f;

        public YoloOnnxDetector(string modelPath, string[] classNames)
        {
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"ONNX model not found: {modelPath}");

            _session = new InferenceSession(modelPath);
            _classNames = classNames ?? Array.Empty<string>();
        }

        /// <summary>
        /// 检测图片，返回画好框的图片路径
        /// </summary>
        /// <param name="imagePath">输入图片路径</param>
        /// <param name="outputPath">输出结果图路径</param>
        /// <param name="filterClassName">只保留这个类别（null 表示保留所有）</param>
        public DetectionResult Detect(string imagePath, string outputPath, string filterClassName = null)
        {
            var result = new DetectionResult();

            using var src = Cv2.ImRead(imagePath);
            if (src.Empty())
                throw new Exception($"Failed to load image: {imagePath}");

            // 1. 预处理：resize + 归一化
            int origW = src.Width;
            int origH = src.Height;

            using var resized = new Mat();
            Cv2.Resize(src, resized, new OpenCvSharp.Size(_inputWidth, _inputHeight));

            var inputTensor = PreprocessImage(resized);

            // 2. 推理
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), inputTensor)
            };

            using var outputs = _session.Run(inputs);
            var output = outputs.First().AsTensor<float>();

            // 3. 后处理：解析检测框、NMS
            var detections = Postprocess(output, origW, origH);

            // 4. 只保留指定类别
            if (!string.IsNullOrEmpty(filterClassName))
            {
                detections = detections
                    .Where(d => string.Equals(d.ClassName, filterClassName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // 重新编号
                for (int i = 0; i < detections.Count; i++)
                {
                    detections[i].Index = i + 1;
                }
            }

            // 5. 画框
            foreach (var det in detections)
            {
                var rect = new Rect(det.X, det.Y, det.Width, det.Height);
                Cv2.Rectangle(src, rect, new Scalar(0, 255, 0), 2);

                string label = $"[{det.Index}] {det.ClassName} {det.Confidence:F2}";
                Cv2.PutText(src, label, new Point(det.X, det.Y - 5),
                    HersheyFonts.HersheySimplex, 0.6, new Scalar(0, 255, 0), 2);
            }

            // 6. 保存结果图
            Cv2.ImWrite(outputPath, src);

            result.OutputPath = outputPath;
            result.Detections = detections;
            result.MaxConfidence = detections.Count > 0 ? detections.Max(d => d.Confidence) : 0;

            // 7. 按类别统计
            result.ClassCounts = detections
                .GroupBy(d => d.ClassName)
                .ToDictionary(g => g.Key, g => g.Count());

            return result;
        }

        /// <summary>
        /// 图片预处理：转成 ONNX 输入的 Tensor
        /// </summary>
        private DenseTensor<float> PreprocessImage(Mat img)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, _inputHeight, _inputWidth });

            for (int y = 0; y < _inputHeight; y++)
            {
                for (int x = 0; x < _inputWidth; x++)
                {
                    var pixel = img.At<Vec3b>(y, x);
                    // BGR → RGB，归一化到 0~1
                    tensor[0, 0, y, x] = pixel.Item2 / 255f;   // R
                    tensor[0, 1, y, x] = pixel.Item1 / 255f;   // G
                    tensor[0, 2, y, x] = pixel.Item0 / 255f;   // B
                }
            }

            return tensor;
        }

        /// <summary>
        /// 后处理：解析 YOLO 输出
        /// </summary>
        private List<Detection> Postprocess(Tensor<float> output, int origW, int origH)
        {
            var detections = new List<Detection>();

            // YOLOv8/v11 输出形状：[1, 4 + numClasses, 8400]
            var dims = output.Dimensions;

            int numClasses = dims[1] - 4;
            int numBoxes = dims[2];

            float xScale = (float)origW / _inputWidth;
            float yScale = (float)origH / _inputHeight;

            for (int i = 0; i < numBoxes; i++)
            {
                // 找最大类别概率
                float maxConf = 0;
                int maxClass = 0;

                for (int c = 0; c < numClasses; c++)
                {
                    float conf = output[0, 4 + c, i];
                    if (conf > maxConf)
                    {
                        maxConf = conf;
                        maxClass = c;
                    }
                }

                if (maxConf < _confThreshold) continue;

                // 获取框坐标（中心点 + 宽高）
                float cx = output[0, 0, i];
                float cy = output[0, 1, i];
                float w = output[0, 2, i];
                float h = output[0, 3, i];

                // 转换到原图坐标
                int x = (int)((cx - w / 2) * xScale);
                int y = (int)((cy - h / 2) * yScale);
                int boxW = (int)(w * xScale);
                int boxH = (int)(h * yScale);

                detections.Add(new Detection
                {
                    X = Math.Max(0, x),
                    Y = Math.Max(0, y),
                    Width = boxW,
                    Height = boxH,
                    ClassId = maxClass,
                    ClassName = maxClass < _classNames.Length ? _classNames[maxClass] : $"class_{maxClass}",
                    Confidence = maxConf
                });
            }

            // NMS 去重
            return NMS(detections);
        }

        /// <summary>
        /// NMS 非极大值抑制
        /// </summary>
        private List<Detection> NMS(List<Detection> detections)
        {
            var result = new List<Detection>();
            var sorted = detections.OrderByDescending(d => d.Confidence).ToList();

            while (sorted.Count > 0)
            {
                var best = sorted[0];
                best.Index = result.Count + 1;
                result.Add(best);
                sorted.RemoveAt(0);

                sorted.RemoveAll(d => IoU(best, d) > _iouThreshold);
            }

            return result;
        }

        private float IoU(Detection a, Detection b)
        {
            int x1 = Math.Max(a.X, b.X);
            int y1 = Math.Max(a.Y, b.Y);
            int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            int interW = Math.Max(0, x2 - x1);
            int interH = Math.Max(0, y2 - y1);
            float interArea = interW * interH;

            float unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
            return unionArea <= 0 ? 0 : interArea / unionArea;
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }

    public class Detection
    {
        public int Index { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ClassId { get; set; }
        public string ClassName { get; set; }
        public float Confidence { get; set; }
    }

    public class DetectionResult
    {
        public string OutputPath { get; set; }
        public List<Detection> Detections { get; set; } = new();
        public float MaxConfidence { get; set; }
        public Dictionary<string, int> ClassCounts { get; set; } = new();
    }
}