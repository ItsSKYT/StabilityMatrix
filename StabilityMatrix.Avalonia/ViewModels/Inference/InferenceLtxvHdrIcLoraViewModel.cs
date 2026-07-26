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

/// <summary>
/// HDR IC-LoRA video-to-video. Loads HDR LoRA via Extra Networks; outputs frames (EXR if Comfy supports, else PNG).
/// </summary>
[View(typeof(InferenceLtxvHdrIcLoraView), IsPersistent = true)]
[RegisterScoped<InferenceLtxvHdrIcLoraViewModel>, ManagedService]
public class InferenceLtxvHdrIcLoraViewModel : InferenceLtxvTextToVideoViewModel
{
    public InferenceLtxvHdrIcLoraViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        SelectVideoCardViewModel = vmFactory.Get<SelectVideoCardViewModel>();
        StackCardViewModel.AddCards(SelectVideoCardViewModel);
        VideoOutputSettingsCardViewModel.AddAudio = false;
        AdvancedOptionsCardViewModel.EnableTwoStage = true;
        SamplerCardViewModel.IsDenoiseStrengthEnabled = true;
        SamplerCardViewModel.DenoiseStrength = 0.7;
    }

    [JsonPropertyName("VideoLoader")]
    public SelectVideoCardViewModel SelectVideoCardViewModel { get; }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        var applyArgs = args.ToModuleApplyStepEventArgs();
        var builder = args.Builder;

        builder.Connections.Seed = args.SeedOverride switch
        {
            { } seed => Convert.ToUInt64(seed),
            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
        };

        VideoOutputSettingsCardViewModel.ApplyEarlyConnections(builder);
        builder.Connections.UseLtxNativeAudio = false;
        ModelCardViewModel.ApplyStep(applyArgs);

        var video =
            SelectVideoCardViewModel.LoadVideoNode(applyArgs)
            ?? throw new ValidationException("Select a source video for HDR IC-LoRA");

        var components = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.GetVideoComponents
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.GetVideoComponents)),
                Video = video,
            }
        );
        builder.Connections.Primary = components.Output1;

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

        SamplerCardViewModel.IsDenoiseStrengthEnabled = true;
        SamplerCardViewModel.ApplyStep(applyArgs);
        applyArgs.InvokeAllPreOutputActions();
        VideoOutputSettingsCardViewModel.ApplyStep(applyArgs);
    }
}
