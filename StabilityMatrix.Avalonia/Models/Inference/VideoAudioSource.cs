namespace StabilityMatrix.Avalonia.Models.Inference;

public enum VideoAudioSource
{
    /// <summary>Native joint audio from LTX 2.x (requires Audio VAE + AV latent path).</summary>
    Ltx,

    /// <summary>Post-hoc Foley via ComfyUI-MMAudio.</summary>
    MMAudio,
}
