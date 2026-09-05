namespace StabilityMatrix.Core.Models.PromptGenerator;

/// <summary>
/// A single Hugging Face file that must be present before inference can start.
/// </summary>
public sealed record PromptGeneratorFileSpec
{
    /// <summary>Hugging Face repo id, e.g. <c>microsoft/Florence-2-large</c>.</summary>
    public required string Repository { get; init; }

    /// <summary>Path of the file inside the repository (may include subdirectories).</summary>
    public required string RemotePath { get; init; }

    /// <summary>Local file name under the model directory. Defaults to the remote file name.</summary>
    public string? LocalFileName { get; init; }

    public string FileName => LocalFileName ?? Path.GetFileName(RemotePath);

    public Uri DownloadUri =>
        new($"https://huggingface.co/{Repository}/resolve/main/{RemotePath.Replace('\\', '/')}?download=true");
}
