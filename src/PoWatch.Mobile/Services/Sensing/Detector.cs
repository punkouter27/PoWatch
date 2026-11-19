using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PoWatch.Shared.Services;

namespace PoWatch.Mobile.Services.Sensing;

/// <summary>
/// On-device object detection with the same model the browser runs — RF-DETR nano (Apache-2.0,
/// onnx-community/rfdetr_nano-ONNX, int8) — through ONNX Runtime, on the phone's NPU/GPU where the
/// platform offers one (NNAPI on Android, Core ML on iOS) and the CPU otherwise. Same COCO labels, same
/// 0.5 threshold, same normalised boxes and colour signature, so the tracker and the server see the same
/// detections a browser would produce.
/// </summary>
public sealed class Detector : IDisposable
{
    public const int InputSize = 384;
    public const double Threshold = 0.5;

    private InferenceSession? _session;
    private string? _inputName, _logitsName, _boxesName;

    public bool IsReady => _session is not null;

    public string? Accelerator { get; private set; }

    public void Load(string modelPath)
    {
        if (_session is not null)
        {
            return;
        }

        // Prefer the hardware delegate; fall back to the CPU when it refuses the model.
        foreach (string accel in new[] { PlatformAccelerator, "CPU" })
        {
            try
            {
                using SessionOptions options = new() { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = 2 };
#if ANDROID
                if (accel == "NNAPI") options.AppendExecutionProvider_Nnapi(NnapiFlags.NNAPI_FLAG_USE_FP16);
#elif IOS
                if (accel == "CoreML") options.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_ENABLE_ON_SUBGRAPH);
#endif
                _session = new InferenceSession(modelPath, options);
                Accelerator = accel;
                break;
            }
            catch (OnnxRuntimeException) when (accel != "CPU")
            {
            }
        }

        _inputName = _session!.InputMetadata.Keys.First();
        // Pick outputs by shape: boxes end in 4 (cx, cy, w, h); the other is the class logits.
        foreach ((string name, NodeMetadata meta) in _session.OutputMetadata)
        {
            if (meta.Dimensions[^1] == 4)
            {
                _boxesName = name;
            }
            else
            {
                _logitsName = name;
            }
        }
    }

    private static string PlatformAccelerator =>
#if ANDROID
        "NNAPI";
#elif IOS
        "CoreML";
#else
        "CPU";
#endif

    /// <summary>Detections at or above <see cref="Threshold"/>, boxes normalised to [0, 1], with signatures; and the inference time.</summary>
    public (List<Detection> Detections, double LatencyMs) Detect(RgbaFrame frame)
    {
        if (_session is null)
        {
            return ([], 0);
        }

        DenseTensor<float> input = Preprocess(frame);
        long started = Stopwatch.GetTimestamp();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName!, input)]);
        double latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        Tensor<float> logits = results.First(r => r.Name == _logitsName).AsTensor<float>();
        Tensor<float> boxes = results.First(r => r.Name == _boxesName).AsTensor<float>();
        int queries = logits.Dimensions[1], classes = logits.Dimensions[2];
        List<Detection> found = [];

        for (int q = 0; q < queries; q++)
        {
            int best = -1;
            float bestLogit = float.MinValue;
            for (int c = 0; c < classes; c++)
            {
                float v = logits[0, q, c];
                if (v > bestLogit)
                {
                    bestLogit = v;
                    best = c;
                }
            }

            double score = 1 / (1 + Math.Exp(-bestLogit));
            if (score < Threshold || CocoLabels.For(best) is not { } label)
            {
                continue;
            }

            double cx = boxes[0, q, 0], cy = boxes[0, q, 1], w = boxes[0, q, 2], h = boxes[0, q, 3];
            double x0 = Math.Clamp(cx - w / 2, 0, 1), y0 = Math.Clamp(cy - h / 2, 0, 1), x1 = Math.Clamp(cx + w / 2, 0, 1), y1 = Math.Clamp(cy + h / 2, 0, 1);
            found.Add(new Detection(label, score, x0, y0, x1, y1, Signature(frame, x0, y0, x1, y1)));
        }

        return (found, latency);
    }

    // Bilinear-free nearest resize to 384×384, RGB scaled to [0, 1], no mean/std (do_normalize: false).
    private static DenseTensor<float> Preprocess(RgbaFrame frame)
    {
        DenseTensor<float> tensor = new([1, 3, InputSize, InputSize]);
        Span<float> data = tensor.Buffer.Span;
        const int plane = InputSize * InputSize;
        for (int y = 0; y < InputSize; y++)
        {
            int sy = Math.Min(frame.Height - 1, (int)((y + 0.5) * frame.Height / InputSize));
            for (int x = 0; x < InputSize; x++)
            {
                int sx = Math.Min(frame.Width - 1, (int)((x + 0.5) * frame.Width / InputSize));
                int s = (sy * frame.Width + sx) * 4, o = y * InputSize + x;
                data[o] = frame.Pixels[s] / 255f;
                data[plane + o] = frame.Pixels[s + 1] / 255f;
                data[(2 * plane) + o] = frame.Pixels[s + 2] / 255f;
            }
        }

        return tensor;
    }

    /// <summary>
    /// The web's appearance signature for recognising regulars: a 64-bin colour histogram (4 levels per
    /// channel) of the centre 60% of the box, every second pixel, rounded to 4 places. Colours only.
    /// </summary>
    public static float[] Signature(RgbaFrame frame, double x0n, double y0n, double x1n, double y1n)
    {
        int width = frame.Width, height = frame.Height;
        static int Clamp(double v, int max) => Math.Max(0, Math.Min(max - 1, (int)Math.Round(v)));
        double bw = (x1n - x0n) * width, bh = (y1n - y0n) * height;
        int x0 = Clamp(x0n * width + bw * 0.2, width), x1 = Clamp(x1n * width - bw * 0.2, width);
        int y0 = Clamp(y0n * height + bh * 0.2, height), y1 = Clamp(y1n * height - bh * 0.2, height);
        int[] bins = new int[64];
        int n = 0;
        for (int y = y0; y <= y1; y += 2)
        {
            for (int x = x0; x <= x1; x += 2)
            {
                int i = (y * width + x) * 4;
                bins[((frame.Pixels[i] >> 6) << 4) | ((frame.Pixels[i + 1] >> 6) << 2) | (frame.Pixels[i + 2] >> 6)]++;
                n++;
            }
        }

        return n == 0 ? [] : [.. bins.Select(c => (float)(Math.Round(c / (double)n * 10000) / 10000))];
    }

    public void Dispose() => _session?.Dispose();
}

/// <summary>The model's id2label (COCO, 91 slots with gaps) from its config.json.</summary>
internal static class CocoLabels
{
    private static readonly string?[] Labels = Build();

    public static string? For(int id) => id >= 0 && id < Labels.Length ? Labels[id] : null;

    private static string?[] Build()
    {
        string?[] l = new string?[91];
        string[] names =
        [
            "1:person", "2:bicycle", "3:car", "4:motorcycle", "5:airplane", "6:bus", "7:train", "8:truck", "9:boat", "10:traffic light",
            "11:fire hydrant", "13:stop sign", "14:parking meter", "15:bench", "16:bird", "17:cat", "18:dog", "19:horse", "20:sheep", "21:cow",
            "22:elephant", "23:bear", "24:zebra", "25:giraffe", "27:backpack", "28:umbrella", "31:handbag", "32:tie", "33:suitcase", "34:frisbee",
            "35:skis", "36:snowboard", "37:sports ball", "38:kite", "39:baseball bat", "40:baseball glove", "41:skateboard", "42:surfboard",
            "43:tennis racket", "44:bottle", "46:wine glass", "47:cup", "48:fork", "49:knife", "50:spoon", "51:bowl", "52:banana", "53:apple",
            "54:sandwich", "55:orange", "56:broccoli", "57:carrot", "58:hot dog", "59:pizza", "60:donut", "61:cake", "62:chair", "63:couch",
            "64:potted plant", "65:bed", "67:dining table", "70:toilet", "72:tv", "73:laptop", "74:mouse", "75:remote", "76:keyboard",
            "77:cell phone", "78:microwave", "79:oven", "80:toaster", "81:sink", "82:refrigerator", "84:book", "85:clock", "86:vase",
            "87:scissors", "88:teddy bear", "89:hair drier", "90:toothbrush",
        ];
        foreach (string entry in names)
        {
            int colon = entry.IndexOf(':', StringComparison.Ordinal);
            l[int.Parse(entry[..colon], System.Globalization.CultureInfo.InvariantCulture)] = entry[(colon + 1)..];
        }

        return l;
    }
}
