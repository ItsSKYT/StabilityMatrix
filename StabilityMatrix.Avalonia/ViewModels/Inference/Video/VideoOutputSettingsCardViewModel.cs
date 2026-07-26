using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Injectio.Attributes;
using OneOf;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Video;

[View(typeof(VideoOutputSettingsCard))]
[ManagedService]
[RegisterTransient<VideoOutputSettingsCardViewModel>]
public partial class VideoOutputSettingsCardViewModel
    : LoadableViewModelBase,
        IParametersLoadableState,
        IComfyStep
{
    // Default MMAudio fp16 bundle (Kijai/MMAudio_safetensors)
    private const string DefaultMmaudioModel = "mmaudio_large_44k_v2_fp16.safetensors";
    private const string DefaultMmaudioVae = "mmaudio_vae_44k_fp16.safetensors";
    private const string DefaultMmaudioSynchformer = "mmaudio_synchformer_fp16.safetensors";
    private const string DefaultMmaudioClip = "apple_DFN5B-CLIP-ViT-H-14-384_fp16.safetensors";

    [ObservableProperty]
    private double fps = 24;

    [ObservableProperty]
    private VideoOutputFormat selectedFormat = VideoOutputFormat.FfmpegMp4;

    [ObservableProperty]
    private bool lossless = true;

    [ObservableProperty]
    private int quality = 90;

    [ObservableProperty]
    private VideoOutputMethod selectedMethod = VideoOutputMethod.Default;

    [ObservableProperty]
    private bool addAudio;

    [ObservableProperty]
    private VideoAudioSource selectedAudioSource = VideoAudioSource.Ltx;

    /// <summary>When true, UI offers LTX native audio (LTX inference pages).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool supportsLtxNativeAudio;

    /// <summary>Text-to-audio: skip video frame encode, only SaveAudio.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool audioOnlyMode;

    [JsonIgnore]
    public List<VideoOutputFormat> AvailableFormats { get; } =
        [VideoOutputFormat.FfmpegMp4, VideoOutputFormat.Webp];

    [JsonIgnore]
    public List<VideoOutputMethod> AvailableMethods { get; } =
        Enum.GetValues<VideoOutputMethod>().ToList();

    [JsonIgnore]
    public List<VideoAudioSource> AvailableAudioSources { get; } =
        Enum.GetValues<VideoAudioSource>().ToList();

    public bool IsWebpFormat => SelectedFormat == VideoOutputFormat.Webp;

    public bool ShowQuality => !Lossless;

    public bool ShowWebpMethod => IsWebpFormat && !AddAudio;

    public bool ShowAudioSource => AddAudio && SupportsLtxNativeAudio;

    private bool UsesFfmpegEncode =>
        AddAudio
        || SelectedFormat is VideoOutputFormat.FfmpegMp4 or VideoOutputFormat.Mp4;

    private bool UseLtxNativeAudio =>
        AddAudio
        && SupportsLtxNativeAudio
        && SelectedAudioSource == VideoAudioSource.Ltx;

    private bool UseMMAudio =>
        AddAudio && (!SupportsLtxNativeAudio || SelectedAudioSource == VideoAudioSource.MMAudio);

    partial void OnSelectedFormatChanged(VideoOutputFormat value)
    {
        if (value == VideoOutputFormat.Mp4)
        {
            SelectedFormat = VideoOutputFormat.FfmpegMp4;
            return;
        }

        OnPropertyChanged(nameof(IsWebpFormat));
        OnPropertyChanged(nameof(ShowWebpMethod));
    }

    partial void OnLosslessChanged(bool value) => OnPropertyChanged(nameof(ShowQuality));

    partial void OnAddAudioChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWebpMethod));
        OnPropertyChanged(nameof(ShowAudioSource));
    }

    partial void OnSupportsLtxNativeAudioChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAudioSource));
        if (value && SelectedAudioSource is not VideoAudioSource.Ltx and not VideoAudioSource.MMAudio)
            SelectedAudioSource = VideoAudioSource.Ltx;
    }

    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        Fps = parameters.OutputFps > 0 ? parameters.OutputFps : Fps;
        Lossless = parameters.Lossless;
        Quality = parameters.VideoQuality > 0 ? parameters.VideoQuality : Quality;
        AddAudio = parameters.AddVideoAudio;

        if (
            !string.IsNullOrWhiteSpace(parameters.VideoAudioSource)
            && Enum.TryParse<VideoAudioSource>(parameters.VideoAudioSource, true, out var audioSource)
        )
        {
            SelectedAudioSource = audioSource;
        }

        if (string.IsNullOrWhiteSpace(parameters.VideoOutputMethod))
            return;

        if (
            parameters.VideoOutputMethod.Equals("Mp4", StringComparison.OrdinalIgnoreCase)
            || parameters.VideoOutputMethod.Equals("FfmpegMp4", StringComparison.OrdinalIgnoreCase)
        )
        {
            SelectedFormat = VideoOutputFormat.FfmpegMp4;
            return;
        }

        if (Enum.TryParse<VideoOutputFormat>(parameters.VideoOutputMethod, true, out var format))
        {
            SelectedFormat = format == VideoOutputFormat.Mp4 ? VideoOutputFormat.FfmpegMp4 : format;
            return;
        }

        if (Enum.TryParse<VideoOutputMethod>(parameters.VideoOutputMethod, true, out var method))
        {
            SelectedFormat = VideoOutputFormat.Webp;
            SelectedMethod = method;
        }
    }

    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        return parameters with
        {
            OutputFps = Fps,
            Lossless = Lossless,
            VideoQuality = Quality,
            AddVideoAudio = AddAudio,
            VideoAudioSource = AddAudio
                ? (UseLtxNativeAudio ? nameof(VideoAudioSource.Ltx) : nameof(VideoAudioSource.MMAudio))
                : null,
            VideoOutputMethod = UsesFfmpegEncode
                ? nameof(VideoOutputFormat.FfmpegMp4)
                : SelectedFormat.ToString(),
        };
    }

    /// <summary>Call before sampler so LTX can build AV latents.</summary>
    public void ApplyEarlyConnections(ComfyNodeBuilder builder)
    {
        builder.Connections.VideoOutputFps = Fps;
        builder.Connections.UseLtxNativeAudio = UseLtxNativeAudio;
    }

    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        if (AudioOnlyMode)
        {
            ApplyLtxNativeAudioSave(e);
            return;
        }

        if (e.Builder.Connections.Primary is null)
            throw new ArgumentException("No Primary");

        var image = e.Builder.Connections.Primary.Match(
            _ =>
                e.Builder.GetPrimaryAsImage(
                    e.Builder.Connections.PrimaryVAE
                        ?? e.Builder.Connections.Refiner.VAE
                        ?? e.Builder.Connections.Base.VAE
                        ?? throw new ArgumentException("No Primary, Refiner, or Base VAE")
                ),
            image => image
        );

        if (UsesFfmpegEncode && !AudioOnlyMode)
        {
            var saveFrames = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.SaveImage
                {
                    Name = e.Nodes.GetUniqueName("SaveImage"),
                    Images = image,
                    FilenamePrefix = "temp/sm_vid_frames",
                }
            );
            e.Builder.Connections.OutputNodes.Add(saveFrames);
        }
        else if (!UsesFfmpegEncode && !AudioOnlyMode)
        {
            var webpStep = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.SaveAnimatedWEBP
                {
                    Name = e.Nodes.GetUniqueName("SaveAnimatedWEBP"),
                    Images = image,
                    FilenamePrefix = "InferenceVideo",
                    Fps = Fps,
                    Lossless = Lossless,
                    Quality = Quality,
                    Method = SelectedMethod.ToString().ToLowerInvariant(),
                }
            );
            e.Builder.Connections.OutputNodes.Add(webpStep);
        }

        if (!AddAudio && !AudioOnlyMode)
            return;

        if (UseLtxNativeAudio || AudioOnlyMode)
        {
            ApplyLtxNativeAudioSave(e);
            return;
        }

        if (UseMMAudio)
            ApplyMMAudioSave(e, image);
    }

    private static void ApplyLtxNativeAudioSave(ModuleApplyStepEventArgs e)
    {
        AudioNodeConnection audioOut;
        if (e.Builder.Connections.LtxPassthroughAudio is { } passthrough)
        {
            audioOut = passthrough;
        }
        else
        {
            var audioLatent =
                e.Builder.Connections.LtxAudioLatent
                ?? throw new ValidationException(
                    "LTX audio was selected but no audio latent was produced. Use an LTX Text/Image-to-Video tab."
                );
            var audioVae =
                e.Builder.Connections.LtxAudioVae
                ?? throw new ValidationException(
                    "LTX Audio VAE missing. Place LTX23_audio_vae_bf16.safetensors in Checkpoints."
                );

            audioOut = e
                .Nodes.AddTypedNode(
                    new ComfyNodeBuilder.LTXVAudioVAEDecode
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVAudioVAEDecode)),
                        Samples = audioLatent,
                        AudioVae = audioVae,
                    }
                )
                .Output;
        }

        var saveAudio = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.SaveAudio
            {
                Name = e.Nodes.GetUniqueName("SaveAudio"),
                Audio = audioOut,
                FilenamePrefix = "temp/sm_vid_audio",
            }
        );

        e.Builder.Connections.OutputNodes.Add(saveAudio);
    }

    private void ApplyMMAudioSave(ModuleApplyStepEventArgs e, ImageNodeConnection image)
    {
        var frameCount = Math.Max(1, e.Builder.Connections.VideoFrameCount);
        var durationSec = Math.Max(0.5, frameCount / Math.Max(1.0, Fps));

        var mmModel = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.MMAudioModelLoader
            {
                Name = e.Nodes.GetUniqueName("MMAudioModelLoader"),
                MmaudioModel = DefaultMmaudioModel,
                BasePrecision = "fp16",
            }
        );

        var mmFeatures = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.MMAudioFeatureUtilsLoader
            {
                Name = e.Nodes.GetUniqueName("MMAudioFeatureUtilsLoader"),
                VaeModel = DefaultMmaudioVae,
                SynchformerModel = DefaultMmaudioSynchformer,
                ClipModel = DefaultMmaudioClip,
                Mode = "44k",
                Precision = "fp16",
            }
        );

        var mmAudio = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.MMAudioSampler
            {
                Name = e.Nodes.GetUniqueName("MMAudioSampler"),
                MmaudioModel = mmModel.Output,
                FeatureUtils = mmFeatures.Output,
                Duration = durationSec,
                Steps = 25,
                Cfg = 4.5,
                Seed = unchecked((long)e.Builder.Connections.Seed),
                Prompt = ResolvePromptText(e.Builder.Connections.PositivePrompt),
                NegativePrompt = ResolvePromptText(e.Builder.Connections.NegativePrompt),
                MaskAwayClip = false,
                ForceOffload = true,
                Images = image,
            }
        );

        var saveAudio = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.SaveAudio
            {
                Name = e.Nodes.GetUniqueName("SaveAudio"),
                Audio = mmAudio.Output,
                FilenamePrefix = "temp/sm_vid_audio",
            }
        );

        e.Builder.Connections.OutputNodes.Add(saveAudio);
    }

    private static string ResolvePromptText(OneOf<string, StringNodeConnection> prompt) =>
        prompt.Match(text => text ?? string.Empty, _ => string.Empty);
}
