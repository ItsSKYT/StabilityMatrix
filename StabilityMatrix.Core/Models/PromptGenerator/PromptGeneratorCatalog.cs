namespace StabilityMatrix.Core.Models.PromptGenerator;

/// <summary>
/// Built-in Image Prompt Generator models. Inference runs through ComfyUI custom nodes;
/// missing weights are pulled from Hugging Face on first generate.
/// </summary>
public static class PromptGeneratorCatalog
{
    public static PromptGeneratorModelDefinition Get(PromptGeneratorModelId id) =>
        All.First(model => model.Id == id);

    public static PromptGeneratorModelDefinition JoyCaptionAlphaTwo { get; } =
        new()
        {
            Id = PromptGeneratorModelId.JoyCaptionAlphaTwo,
            FolderName = "JoyCaptionAlphaTwo",
            DisplayName = "JoyCaption Alpha Two (NF4)",
            Description =
                "4-bit NF4 JoyCaption Alpha Two (CLIP/SigLIP). ComfyUI-JoyCaption downloads the checkpoint on first run.",
            Backend = PromptGeneratorBackend.JoyCaption,
            ComfyModelName = "joycaption-alpha-two",
            RequiredExtensionUrls = ["https://github.com/1038lab/ComfyUI-JoyCaption"],
            Files = [],
        };

    public static PromptGeneratorModelDefinition Qwen25Vl7BAbliterated { get; } =
        new()
        {
            Id = PromptGeneratorModelId.Qwen25Vl7BAbliterated,
            FolderName = "Qwen25VL7BAbliterated",
            DisplayName = "Qwen2.5-VL 7B Abliterated (Q4_K_M)",
            Description =
                "Uncensored Qwen2.5-VL-7B GGUF Q4_K_M + mmproj (huihui-ai via mradermacher). Does not refuse NSFW or complex scenes.",
            Backend = PromptGeneratorBackend.QwenVlGguf,
            ComfyModelName = "Qwen2.5-VL-7B-Instruct-abliterated.Q4_K_M.gguf",
            RelativeModelsPath = "LLM/GGUF/mradermacher/Qwen2.5-VL-7B-Instruct-abliterated-GGUF",
            RequiredExtensionUrls = ["https://github.com/1038lab/ComfyUI-QwenVL"],
            Files =
            [
                Hf(
                    "mradermacher/Qwen2.5-VL-7B-Instruct-abliterated-GGUF",
                    "Qwen2.5-VL-7B-Instruct-abliterated.Q4_K_M.gguf"
                ),
                Hf(
                    "mradermacher/Qwen2.5-VL-7B-Instruct-abliterated-GGUF",
                    "Qwen2.5-VL-7B-Instruct-abliterated.mmproj-f16.gguf"
                ),
            ],
        };

    public static PromptGeneratorModelDefinition Florence2Large { get; } =
        new()
        {
            Id = PromptGeneratorModelId.Florence2Large,
            FolderName = "Florence2Large",
            DisplayName = "Florence-2 Large",
            Description =
                "microsoft/Florence-2-large via ComfyUI-Florence2. First run downloads the Hub snapshot into the LLM folder.",
            Backend = PromptGeneratorBackend.Florence2,
            ComfyModelName = "microsoft/Florence-2-large",
            RequiredExtensionUrls = ["https://github.com/kijai/ComfyUI-Florence2"],
            Files = [],
        };

    public static IReadOnlyList<PromptGeneratorModelDefinition> All { get; } =
        [JoyCaptionAlphaTwo, Qwen25Vl7BAbliterated, Florence2Large];

    private static PromptGeneratorFileSpec Hf(string repository, string remotePath) =>
        new() { Repository = repository, RemotePath = remotePath };
}
