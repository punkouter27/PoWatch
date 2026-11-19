using System.Net.Http.Headers;

namespace PoWatch.Mobile.Services.Sensing;

/// <summary>
/// Fetches the detector model once (~29 MB, the same file the browser caches) into the app's private
/// storage. An interrupted download resumes where it stopped (HTTP Range), so a flaky connection never
/// restarts it from zero.
/// </summary>
public sealed class ModelStore
{
    public const string ModelUrl = "https://huggingface.co/onnx-community/rfdetr_nano-ONNX/resolve/main/onnx/model_quantized.onnx";
    private const long MinimumBytes = 20_000_000;

    private static readonly HttpClient Download = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string ModelPath => Path.Combine(FileSystem.AppDataDirectory, "models", "rfdetr_nano_q8.onnx");

    public static bool IsDownloaded => File.Exists(ModelPath);

    /// <summary>Downloads (or finishes downloading) the model; progress is 0–1. Returns the path.</summary>
    public static async Task<string> EnsureAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (IsDownloaded)
        {
            return ModelPath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        string partial = ModelPath + ".part";
        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using HttpRequestMessage request = new(HttpMethod.Get, ModelUrl);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        using HttpResponseMessage resp = await Download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.OK)
        {
            have = 0; // the server ignored the range: start over
        }

        resp.EnsureSuccessStatusCode();
        long total = have + (resp.Content.Headers.ContentLength ?? 0);
        await using (FileStream file = new(partial, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write))
        await using (Stream body = await resp.Content.ReadAsStreamAsync(ct))
        {
            byte[] buffer = new byte[1 << 16];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                have += read;
                if (total > 0)
                {
                    progress?.Report(have / (double)total);
                }
            }
        }

        if (new FileInfo(partial).Length < MinimumBytes)
        {
            File.Delete(partial);
            throw new IOException("The model download was incomplete.");
        }

        File.Move(partial, ModelPath, overwrite: true);
        return ModelPath;
    }
}
