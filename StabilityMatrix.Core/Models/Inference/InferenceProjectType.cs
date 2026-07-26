namespace StabilityMatrix.Core.Models.Inference;

public enum InferenceProjectType
{
    Unknown,
    TextToImage,
    ImageToImage,
    Inpainting,
    Upscale,
    ImageToVideo,
    FluxTextToImage,
    WanTextToVideo,
    WanImageToVideo,
    QwenImageEdit,
    Flux2KleinImageEdit,
    ImagePrompt,
    Krea2ImageEdit,
    LtxvTextToVideo,
    LtxvImageToVideo,
}
