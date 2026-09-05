using Injectio.Attributes;
using Microsoft.Extensions.Logging;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Models.PromptGenerator;

namespace StabilityMatrix.Core.Services;

[RegisterSingleton<IPromptGeneratorDownloadService, PromptGeneratorDownloadService>]
public sealed class PromptGeneratorDownloadService(
    IDownloadService downloadService,
    ILogger<PromptGeneratorDownloadService> logger
) : IPromptGeneratorDownloadService
{
    private readonly SemaphoreSlim downloadLock = new(1, 1);

    public DirectoryPath GetModelDirectory(DirectoryPath modelsRoot, PromptGeneratorModelDefinition model)
    {
        if (string.IsNullOrWhiteSpace(model.RelativeModelsPath))
        {
            return modelsRoot.JoinDir("LLM", model.FolderName);
        }

        var parts = model.RelativeModelsPath.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        return modelsRoot.JoinDir(parts.Select(part => new DirectoryPath(part)).ToArray());
    }

    public IReadOnlyList<PromptGeneratorFileSpec> GetMissingFiles(
        DirectoryPath modelsRoot,
        PromptGeneratorModelDefinition model
    )
    {
        if (model.Files.Count == 0)
        {
            return [];
        }

        var directory = GetModelDirectory(modelsRoot, model);
        if (!directory.Exists)
        {
            return model.Files;
        }

        return model.Files.Where(file => !IsFileReady(directory, file)).ToList();
    }

    public bool IsModelReady(DirectoryPath modelsRoot, PromptGeneratorModelDefinition model) =>
        GetMissingFiles(modelsRoot, model).Count == 0;

    public async Task EnsureModelAsync(
        DirectoryPath modelsRoot,
        PromptGeneratorModelDefinition model,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (model.Files.Count == 0)
        {
            return;
        }

        var missing = GetMissingFiles(modelsRoot, model);
        if (missing.Count == 0)
        {
            return;
        }

        await downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            missing = GetMissingFiles(modelsRoot, model);
            if (missing.Count == 0)
            {
                return;
            }

            var directory = GetModelDirectory(modelsRoot, model);
            directory.Create();

            for (var index = 0; index < missing.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DownloadFileAsync(
                        directory,
                        missing[index],
                        index + 1,
                        missing.Count,
                        progress,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            downloadLock.Release();
        }
    }

    private async Task DownloadFileAsync(
        DirectoryPath directory,
        PromptGeneratorFileSpec file,
        int fileNumber,
        int fileCount,
        IProgress<ProgressReport>? progress,
        CancellationToken cancellationToken
    )
    {
        if (IsFileReady(directory, file))
        {
            return;
        }

        var destination = new FilePath(directory, file.FileName);
        var partPath = new FilePath(directory, file.FileName + ".part");
        var existingSize = partPath.Exists ? partPath.Info.Length : 0L;

        logger.LogInformation(
            "Downloading {File} from {Repo} ({FileNumber}/{FileCount}), resume from {ExistingSize} bytes",
            file.FileName,
            file.Repository,
            fileNumber,
            fileCount,
            existingSize
        );

        var title = $"Downloading {file.FileName} ({fileNumber}/{fileCount})";

        var fileProgress = new Progress<ProgressReport>(report =>
        {
            var current = report.Current ?? 0;
            var total = report.Total ?? 0;
            var downloadedMb = current / (1024d * 1024d);
            var totalMb = total / (1024d * 1024d);
            var speed = report.SpeedInMBps;

            var message =
                total > 0
                    ? $"{file.FileName} — {downloadedMb:0.0} MB / {totalMb:0.0} MB ({speed:0.0} MB/s)"
                    : $"{file.FileName} — {downloadedMb:0.0} MB ({speed:0.0} MB/s)";

            progress?.Report(
                report with
                {
                    Title = title,
                    Message = message,
                    PrintToConsole = false,
                }
            );
        });

        await downloadService
            .ResumeDownloadToFileAsync(
                file.DownloadUri.ToString(),
                partPath,
                existingSize,
                fileProgress,
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        if (destination.Exists)
        {
            destination.Delete();
        }

        File.Move(partPath, destination);
        progress?.Report(new ProgressReport(1f, title, $"{file.FileName} downloaded"));
    }

    private static bool IsFileReady(DirectoryPath directory, PromptGeneratorFileSpec file)
    {
        var path = new FilePath(directory, file.FileName);
        if (!path.Exists)
        {
            return false;
        }

        path.Info.Refresh();
        return path.Info.Length > 0;
    }
}
