using System;
using StabilityMatrix.Avalonia.ViewModels.Inference;
using StabilityMatrix.Core.Models.Inference;

namespace StabilityMatrix.Avalonia.Extensions;

public static class InferenceProjectTypeExtensions
{
    public static Type? ToViewModelType(this InferenceProjectType type)
    {
        return type switch
        {
            InferenceProjectType.TextToImage => typeof(InferenceTextToImageViewModel),
            InferenceProjectType.ImageToImage => typeof(InferenceImageToImageViewModel),
            InferenceProjectType.Inpainting => null,
            InferenceProjectType.Upscale => typeof(InferenceImageUpscaleViewModel),
            InferenceProjectType.ImageToVideo => typeof(InferenceImageToVideoViewModel),
            InferenceProjectType.FluxTextToImage => typeof(InferenceFluxTextToImageViewModel),
            InferenceProjectType.WanTextToVideo => typeof(InferenceWanTextToVideoViewModel),
            InferenceProjectType.WanImageToVideo => typeof(InferenceWanImageToVideoViewModel),
            InferenceProjectType.QwenImageEdit => typeof(InferenceQwenImageEditViewModel),
            InferenceProjectType.Flux2KleinImageEdit => typeof(InferenceFlux2KleinImageEditViewModel),
            InferenceProjectType.ImagePrompt => typeof(InferenceImagePromptViewModel),
            InferenceProjectType.Krea2ImageEdit => typeof(InferenceKrea2ImageEditViewModel),
            InferenceProjectType.LtxvTextToVideo => typeof(InferenceLtxvTextToVideoViewModel),
            InferenceProjectType.LtxvImageToVideo => typeof(InferenceLtxvImageToVideoViewModel),
            InferenceProjectType.Unknown => null,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }
}
