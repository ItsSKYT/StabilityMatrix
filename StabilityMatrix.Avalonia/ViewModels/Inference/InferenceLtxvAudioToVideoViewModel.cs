using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceLtxvAudioToVideoView), IsPersistent = true)]
[RegisterScoped<InferenceLtxvAudioToVideoViewModel>, ManagedService]
public class InferenceLtxvAudioToVideoViewModel : InferenceLtxvTextToVideoViewModel
{
    public InferenceLtxvAudioToVideoViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        SelectAudioCardViewModel = vmFactory.Get<SelectAudioCardViewModel>();
        SelectImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>();
        VideoOutputSettingsCardViewModel.AddAudio = true;
        VideoOutputSettingsCardViewModel.SelectedAudioSource = VideoAudioSource.Ltx;
        VideoOutputSettingsCardViewModel.SupportsLtxNativeAudio = true;
        StackCardViewModel.AddCards(SelectAudioCardViewModel);
    }

    [JsonPropertyName("AudioLoader")]
    public SelectAudioCardViewModel SelectAudioCardViewModel { get; }

    [JsonPropertyName("ImageLoader")]
    public SelectImageCardViewModel SelectImageCardViewModel { get; }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        var applyArgs = args.ToModuleApplyStepEventArgs();
        var builder = args.Builder;

        builder.Connections.Seed = args.SeedOverride switch
        {
            { } seed => Convert.ToUInt64(seed),
            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
        };

        VideoOutputSettingsCardViewModel.AddAudio = true;
        VideoOutputSettingsCardViewModel.SelectedAudioSource = VideoAudioSource.Ltx;
        VideoOutputSettingsCardViewModel.ApplyEarlyConnections(builder);

        ModelCardViewModel.ApplyStep(applyArgs);

        var audio =
            SelectAudioCardViewModel.LoadAudioNode(applyArgs)
            ?? throw new ValidationException("Select an audio file for Audio-to-Video");

        var audioVae =
            builder.Connections.LtxAudioVae
            ?? throw new ValidationException(
                "LTX Audio VAE required. Place LTX23_audio_vae_bf16.safetensors in Checkpoints."
            );

        var encoded = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVAudioVAEEncode
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVAudioVAEEncode)),
                Audio = audio,
                AudioVae = audioVae,
            }
        );
        builder.Connections.LtxEncodedAudioLatent = encoded.Output;
        builder.Connections.LtxPassthroughAudio = audio;
        builder.Connections.UseLtxNativeAudio = true;
        if (builder.Connections.LikelyConvRotInt4)
            builder.Connections.ForceKitchenEager = true;

        if (SelectImageCardViewModel.ImageSource is not null)
        {
            SamplerCardViewModel.IsDenoiseStrengthEnabled = true;
            var imageLoad = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadImage
                {
                    Name = builder.Nodes.GetUniqueName("LTXV_A2V_LoadImage"),
                    Image = SelectImageCardViewModel.ImageSource.GetHashGuidFileNameCached("Inference"),
                }
            );
            applyArgs.AddFileTransfer(
                SelectImageCardViewModel.ImageSource.LocalFile?.FullPath
                    ?? throw new ValidationException("Image has no local path"),
                SelectImageCardViewModel.ImageSource.GetHashGuidFileNameCached("Inference")
            );
            builder.Connections.Primary = imageLoad.Output1;
            builder.Connections.PrimarySize = SelectImageCardViewModel.CurrentBitmapSize;
        }
        else
        {
            SamplerCardViewModel.IsDenoiseStrengthEnabled = false;
            var (latentW, latentH) = LtxvComfyPipeline.Stage1Size(
                SamplerCardViewModel.Width,
                SamplerCardViewModel.Height,
                ModelCardViewModel.IsLtx25
            );
            builder.SetupEmptyLatentSource(
                latentW,
                latentH,
                BatchSizeCardViewModel.BatchSize,
                BatchSizeCardViewModel.IsBatchIndexEnabled ? BatchSizeCardViewModel.BatchIndex : null,
                SamplerCardViewModel.Length,
                LatentType.Ltxv
            );
        }

        BatchSizeCardViewModel.ApplyStep(applyArgs);
        PromptCardViewModel.ApplyStep(applyArgs);

        var conditioning =
            builder.Connections.Base.Conditioning
            ?? throw new InvalidOperationException("Conditioning not set");
        var ltxCond = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVConditioning
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVConditioning)),
                Positive = conditioning.Positive,
                Negative = conditioning.Negative,
                FrameRate = VideoOutputSettingsCardViewModel.Fps,
            }
        );
        builder.Connections.Base.Conditioning = new ConditioningConnections(
            ltxCond.Output1,
            ltxCond.Output2
        );

        SamplerCardViewModel.ApplyStep(applyArgs);
        applyArgs.InvokeAllPreOutputActions();
        VideoOutputSettingsCardViewModel.ApplyStep(applyArgs);
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (SelectImageCardViewModel.ImageSource is { } image)
            yield return image;
    }
}
