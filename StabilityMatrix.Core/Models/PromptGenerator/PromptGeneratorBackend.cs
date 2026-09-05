namespace StabilityMatrix.Core.Models.PromptGenerator;

/// <summary>
/// Runtime used by the Python worker for a given catalog entry.
/// </summary>
public enum PromptGeneratorBackend
{
    /// <summary>LlavaForConditionalGeneration + bitsandbytes NF4 (CLIP/SigLIP vision tower kept in FP16).</summary>
    JoyCaption,

    /// <summary>llama-cpp-python GGUF + mmproj (Qwen2.5-VL).</summary>
    QwenVlGguf,

    /// <summary>transformers Florence-2 with trust_remote_code.</summary>
    Florence2,
}
