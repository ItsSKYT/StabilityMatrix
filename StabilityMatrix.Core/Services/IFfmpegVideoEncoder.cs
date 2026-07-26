using StabilityMatrix.Core.Models.Progress;

namespace StabilityMatrix.Core.Services;

public interface IFfmpegVideoEncoder
{
    Task<string?> EncodeFramesAsync(
        IReadOnlyList<string> framePaths,
        string outputPathWithoutExtension,
        double fps,
        bool lossless,
        int quality,
        CancellationToken cancellationToken = default
    );

    Task<string?> MuxAudioAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken = default
    );

    Task EnsureFfmpegInstalledAsync(IProgress<ProgressReport>? progress = null);
}
