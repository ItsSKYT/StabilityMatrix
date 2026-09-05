using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Models.PromptGenerator;

namespace StabilityMatrix.Core.Services;

public interface IPromptGeneratorDownloadService
{
    DirectoryPath GetModelDirectory(DirectoryPath modelsRoot, PromptGeneratorModelDefinition model);

    IReadOnlyList<PromptGeneratorFileSpec> GetMissingFiles(
        DirectoryPath modelsRoot,
        PromptGeneratorModelDefinition model
    );

    bool IsModelReady(DirectoryPath modelsRoot, PromptGeneratorModelDefinition model);

    Task EnsureModelAsync(
        DirectoryPath modelsRoot,
        PromptGeneratorModelDefinition model,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default
    );
}
