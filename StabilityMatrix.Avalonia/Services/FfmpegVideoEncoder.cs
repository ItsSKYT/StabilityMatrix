using Injectio.Attributes;
using Microsoft.Extensions.Logging;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.Services;

[RegisterSingleton<IFfmpegVideoEncoder, FfmpegVideoEncoder>]
public class FfmpegVideoEncoder(
    ILogger<FfmpegVideoEncoder> logger,
    IPrerequisiteHelper prerequisiteHelper
) : IFfmpegVideoEncoder
{
    private static readonly SemaphoreSlim FfmpegInstallLock = new(1, 1);

    public async Task EnsureFfmpegInstalledAsync(IProgress<ProgressReport>? progress = null)
    {
        if (await ResolveFfmpegPathAsync().ConfigureAwait(false) is not null)
            return;

        await FfmpegInstallLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!prerequisiteHelper.IsFfmpegInstalled)
            {
                await prerequisiteHelper.InstallFfmpegIfNecessary(progress).ConfigureAwait(false);
            }
        }
        finally
        {
            FfmpegInstallLock.Release();
        }
    }

    public async Task<string?> EncodeFramesAsync(
        IReadOnlyList<string> framePaths,
        string outputPathWithoutExtension,
        double fps,
        bool lossless,
        int quality,
        CancellationToken cancellationToken = default
    )
    {
        if (framePaths.Count == 0)
            return null;

        await EnsureFfmpegInstalledAsync().ConfigureAwait(false);
        var ffmpeg = await ResolveFfmpegPathAsync().ConfigureAwait(false);
        if (ffmpeg is null)
        {
            logger.LogWarning("FFmpeg not available for video encode");
            return null;
        }

        var fpsValue = fps > 0 ? fps : 24;
        var workDir = Path.Combine(Path.GetTempPath(), $"sm-vid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            for (var i = 0; i < framePaths.Count; i++)
            {
                var dest = Path.Combine(workDir, $"frame_{i + 1:D5}{Path.GetExtension(framePaths[i])}");
                File.Copy(framePaths[i], dest, overwrite: true);
            }

            var pattern = Path.Combine(workDir, "frame_%05d.png");
            // If frames aren't png, detect extension from first file
            var ext = Path.GetExtension(framePaths[0]);
            if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                pattern = Path.Combine(workDir, $"frame_%05d{ext}");
            }

            if (lossless)
            {
                var nvencOut = outputPathWithoutExtension + ".mp4";
                if (
                    await TryRunAsync(
                            ffmpeg,
                            BuildNvencLosslessArgs(pattern, fpsValue, nvencOut, "h264_nvenc"),
                            nvencOut,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                    || await TryRunAsync(
                            ffmpeg,
                            BuildNvencLosslessArgs(pattern, fpsValue, nvencOut, "hevc_nvenc"),
                            nvencOut,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                )
                {
                    return nvencOut;
                }

                var webpOut = outputPathWithoutExtension + ".webp";
                if (
                    await TryRunAsync(
                            ffmpeg,
                            BuildWebpLosslessArgs(pattern, fpsValue, webpOut),
                            webpOut,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                )
                {
                    return webpOut;
                }

                return null;
            }

            var lossyOut = outputPathWithoutExtension + ".mp4";
            var cq = QualityToCq(quality);
            if (
                await TryRunAsync(
                        ffmpeg,
                        BuildNvencLossyArgs(pattern, fpsValue, lossyOut, "h264_nvenc", cq),
                        lossyOut,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                || await TryRunAsync(
                        ffmpeg,
                        BuildNvencLossyArgs(pattern, fpsValue, lossyOut, "hevc_nvenc", cq),
                        lossyOut,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                return lossyOut;
            }

            var webpLossy = outputPathWithoutExtension + ".webp";
            if (
                await TryRunAsync(
                        ffmpeg,
                        BuildWebpLossyArgs(pattern, fpsValue, webpLossy, quality),
                        webpLossy,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                return webpLossy;
            }

            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    public async Task<string?> MuxAudioAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(videoPath) || !File.Exists(audioPath))
            return null;

        await EnsureFfmpegInstalledAsync().ConfigureAwait(false);
        var ffmpeg = await ResolveFfmpegPathAsync().ConfigureAwait(false);
        if (ffmpeg is null)
            return null;

        try
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            var args = new ProcessArgsBuilder()
                .AddArg("-nostdin")
                .AddArg("-hide_banner")
                .AddArg("-loglevel")
                .AddArg("error")
                .AddArg("-y")
                .AddArg("-i")
                .AddArg(videoPath)
                .AddArg("-i")
                .AddArg(audioPath)
                // Re-encode video to yuv420p H.264 so Windows players can open the file.
                // (NVENC lossless / 4:4:4 copy often produces unplayable MP4s in WMP.)
                .AddArg("-c:v")
                .AddArg("h264_nvenc")
                .AddArg("-preset")
                .AddArg("p5")
                .AddArg("-rc")
                .AddArg("vbr")
                .AddArg("-cq")
                .AddArg("18")
                .AddArg("-b:v")
                .AddArg("0")
                .AddArg("-pix_fmt")
                .AddArg("yuv420p")
                .AddArg("-c:a")
                .AddArg("aac")
                .AddArg("-b:a")
                .AddArg("192k")
                .AddArg("-af")
                .AddArg("apad")
                .AddArg("-shortest")
                .AddArg("-movflags")
                .AddArg("+faststart")
                .AddArg(outputPath);

            logger.LogInformation("FFmpeg mux: {Ffmpeg} {Args}", ffmpeg, args);
            var result = await ProcessRunner
                .GetProcessResultAsync(ffmpeg, args.ToProcessArgs())
                .ConfigureAwait(false);

            if (result.ExitCode != 0 || !File.Exists(outputPath))
            {
                logger.LogWarning(
                    "FFmpeg NVENC mux failed ({Code}): {Err} — retrying with libx264",
                    result.ExitCode,
                    result.StandardError
                );

                if (File.Exists(outputPath))
                    File.Delete(outputPath);

                var cpuArgs = new ProcessArgsBuilder()
                    .AddArg("-nostdin")
                    .AddArg("-hide_banner")
                    .AddArg("-loglevel")
                    .AddArg("error")
                    .AddArg("-y")
                    .AddArg("-i")
                    .AddArg(videoPath)
                    .AddArg("-i")
                    .AddArg(audioPath)
                    .AddArg("-c:v")
                    .AddArg("libx264")
                    .AddArg("-preset")
                    .AddArg("fast")
                    .AddArg("-crf")
                    .AddArg("18")
                    .AddArg("-pix_fmt")
                    .AddArg("yuv420p")
                    .AddArg("-c:a")
                    .AddArg("aac")
                    .AddArg("-b:a")
                    .AddArg("192k")
                    .AddArg("-af")
                    .AddArg("apad")
                    .AddArg("-shortest")
                    .AddArg("-movflags")
                    .AddArg("+faststart")
                    .AddArg(outputPath);

                result = await ProcessRunner
                    .GetProcessResultAsync(ffmpeg, cpuArgs.ToProcessArgs())
                    .ConfigureAwait(false);

                if (result.ExitCode != 0 || !File.Exists(outputPath))
                {
                    logger.LogWarning(
                        "FFmpeg mux failed ({Code}): {Err}",
                        result.ExitCode,
                        result.StandardError
                    );
                    return null;
                }
            }

            return outputPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FFmpeg mux threw");
            return null;
        }
    }

    private async Task<string?> ResolveFfmpegPathAsync()
    {
        if (prerequisiteHelper.IsFfmpegInstalled && File.Exists(prerequisiteHelper.FfmpegPath))
            return prerequisiteHelper.FfmpegPath;

        try
        {
            var result = await ProcessRunner
                .GetProcessResultAsync(Compat.IsWindows ? "where.exe" : "which", "ffmpeg")
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                var first = result
                    .StandardOutput?.Split(
                        ['\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first) && File.Exists(first))
                    return first;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve ffmpeg from PATH");
        }

        return null;
    }

    private static ProcessArgsBuilder BuildInput(string pattern, double fps) =>
        new ProcessArgsBuilder()
            .AddArg("-nostdin")
            .AddArg("-hide_banner")
            .AddArg("-loglevel")
            .AddArg("error")
            .AddArg("-y")
            .AddArg("-framerate")
            .AddArg(fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .AddArg("-i")
            .AddArg(pattern);

    private static ProcessArgsBuilder BuildNvencLosslessArgs(
        string pattern,
        double fps,
        string output,
        string encoder
    ) =>
        BuildInput(pattern, fps)
            .AddArg("-c:v")
            .AddArg(encoder)
            .AddArg("-preset")
            .AddArg("p7")
            .AddArg("-tune")
            .AddArg("lossless")
            // yuv420p required for Windows Media Player / most players (yuv444p High 4:4:4 is incompatible)
            .AddArg("-pix_fmt")
            .AddArg("yuv420p")
            .AddArg("-an")
            .AddArg(output);

    private static ProcessArgsBuilder BuildNvencLossyArgs(
        string pattern,
        double fps,
        string output,
        string encoder,
        int cq
    ) =>
        BuildInput(pattern, fps)
            .AddArg("-c:v")
            .AddArg(encoder)
            .AddArg("-preset")
            .AddArg("p5")
            .AddArg("-rc")
            .AddArg("vbr")
            .AddArg("-cq")
            .AddArg(cq.ToString())
            .AddArg("-b:v")
            .AddArg("0")
            .AddArg("-pix_fmt")
            .AddArg("yuv420p")
            .AddArg("-an")
            .AddArg(output);

    private static ProcessArgsBuilder BuildWebpLosslessArgs(string pattern, double fps, string output) =>
        BuildInput(pattern, fps)
            .AddArg("-c:v")
            .AddArg("libwebp_anim")
            .AddArg("-lossless")
            .AddArg("1")
            .AddArg("-compression_level")
            .AddArg("4")
            .AddArg("-loop")
            .AddArg("0")
            .AddArg("-an")
            .AddArg(output);

    private static ProcessArgsBuilder BuildWebpLossyArgs(
        string pattern,
        double fps,
        string output,
        int quality
    ) =>
        BuildInput(pattern, fps)
            .AddArg("-c:v")
            .AddArg("libwebp_anim")
            .AddArg("-lossless")
            .AddArg("0")
            .AddArg("-quality")
            .AddArg(Math.Clamp(quality, 1, 100).ToString())
            .AddArg("-compression_level")
            .AddArg("4")
            .AddArg("-loop")
            .AddArg("0")
            .AddArg("-an")
            .AddArg(output);

    private static int QualityToCq(int quality)
    {
        var q = Math.Clamp(quality, 0, 100);
        return (int)Math.Round((100 - q) * 0.51);
    }

    private async Task<bool> TryRunAsync(
        string ffmpeg,
        ProcessArgsBuilder args,
        string expectedOutput,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (File.Exists(expectedOutput))
                File.Delete(expectedOutput);

            logger.LogInformation("FFmpeg encode: {Ffmpeg} {Args}", ffmpeg, args);
            var result = await ProcessRunner
                .GetProcessResultAsync(ffmpeg, args.ToProcessArgs())
                .ConfigureAwait(false);

            if (result.ExitCode != 0 || !File.Exists(expectedOutput))
            {
                logger.LogWarning(
                    "FFmpeg encode failed ({Code}): {Err}",
                    result.ExitCode,
                    result.StandardError
                );
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FFmpeg encode threw");
            return false;
        }
    }
}
