using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceLtxvLipDubView), IsPersistent = true)]
[RegisterScoped<InferenceLtxvLipDubViewModel>, ManagedService]
public class InferenceLtxvLipDubViewModel : InferenceLtxvTextToVideoViewModel
{
    public InferenceLtxvLipDubViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        SelectVideoCardViewModel = vmFactory.Get<SelectVideoCardViewModel>();
        SelectAudioCardViewModel = vmFactory.Get<SelectAudioCardViewModel>();
        StackCardViewModel.AddCards(SelectVideoCardViewModel, SelectAudioCardViewModel);
        VideoOutputSettingsCardViewModel.AddAudio = true;
        VideoOutputSettingsCardViewModel.SelectedAudioSource = VideoAudioSource.Ltx;
        AdvancedOptionsCardViewModel.EnableTwoStage = true;
    }

    [JsonPropertyName("VideoLoader")]
    public SelectVideoCardViewModel SelectVideoCardViewModel { get; }

    [JsonPropertyName("AudioLoader")]
    public SelectAudioCardViewModel SelectAudioCardViewModel { get; }

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

        var video =
            SelectVideoCardViewModel.LoadVideoNode(applyArgs)
            ?? throw new ValidationException("Select a reference video for LipDub");

        var components = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.GetVideoComponents
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.GetVideoComponents)),
                Video = video,
            }
        );

        builder.Connections.Primary = components.Output1;
        AdvancedOptionsCardViewModel.EnableGuideImage = false;
        AdvancedOptionsCardViewModel.EnableReferenceAudio = SelectAudioCardViewModel.HasFile;
        if (SelectAudioCardViewModel.HasFile)
            AdvancedOptionsCardViewModel.ReferenceAudioCard.LocalFile = SelectAudioCardViewModel.LocalFile;

        // New dialogue audio drives generation; reference video guides lips via first-frame I2V + AddGuide chain in sampler
        SamplerCardViewModel.IsDenoiseStrengthEnabled = true;
        SamplerCardViewModel.DenoiseStrength = 0.75;

        if (SelectAudioCardViewModel.HasFile)
        {
            var audio = SelectAudioCardViewModel.LoadAudioNode(applyArgs)!;
            var audioVae =
                builder.Connections.LtxAudioVae
                ?? throw new ValidationException("LTX Audio VAE required for LipDub");
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
}
