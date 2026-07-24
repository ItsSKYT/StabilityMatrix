using System;
using CommunityToolkit.Mvvm.ComponentModel;
using StabilityMatrix.Avalonia.Models;

namespace StabilityMatrix.Avalonia.Models.Inference;

public partial class InferenceQueueItem : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

    public required InferenceProjectDocument Project { get; init; }

    public GenerateFlags Flags { get; init; }

    [ObservableProperty]
    private string promptPreview = string.Empty;

    [ObservableProperty]
    private string? negativePromptPreview;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public static string MakePreview(string? text, int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty prompt)";
        }

        var trimmed = text.ReplaceLineEndings(" ").Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…";
    }
}
