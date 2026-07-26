using System.Text.Json.Serialization;

namespace StabilityMatrix.Avalonia.Models.Inference;

[JsonConverter(typeof(JsonStringEnumConverter<VideoOutputFormat>))]
public enum VideoOutputFormat
{
    /// <summary>PNG frames → FFmpeg (NVENC lossless/high quality, WebP fallback).</summary>
    FfmpegMp4,

    /// <summary>Comfy SaveAnimatedWEBP (CPU).</summary>
    Webp,

    /// <summary>Legacy name from earlier builds; treated as <see cref="FfmpegMp4"/>.</summary>
    Mp4,
}
