namespace StabilityMatrix.Core.Models.PromptGenerator;

public sealed record PromptGeneratorModelDefinition
{
    public required PromptGeneratorModelId Id { get; init; }

    public required string FolderName { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public required PromptGeneratorBackend Backend { get; init; }

    /// <summary>Value passed to the ComfyUI node (dropdown key or Hugging Face repo id).</summary>
    public required string ComfyModelName { get; init; }

    /// <summary>
    /// Path under the Comfy <c>models</c> directory where on-demand files are stored.
    /// Empty when the custom node downloads the snapshot itself.
    /// </summary>
    public string RelativeModelsPath { get; init; } = "";

    public IReadOnlyList<string> RequiredExtensionUrls { get; init; } = [];

    public required IReadOnlyList<PromptGeneratorFileSpec> Files { get; init; }
}
