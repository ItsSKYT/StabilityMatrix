using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// Retake a time window: VideoSlice → frames → ImgToVideo regenerate of that segment.
/// </summary>
[View(typeof(InferenceLtxvVideoRetakeView), IsPersistent = true)]
[RegisterScoped<InferenceLtxvVideoRetakeViewModel>, ManagedService]
public partial class InferenceLtxvVideoRetakeViewModel : InferenceLtxvTextToVideoViewModel
{
    public InferenceLtxvVideoRetakeViewModel(
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
        SamplerCardViewModel.IsDenoiseStrengthEnabled = true;
        SamplerCardViewModel.DenoiseStrength = 0.85;
    }

    [JsonPropertyName("VideoLoader")]
    public SelectVideoCardViewModel SelectVideoCardViewModel { get; }

    [ObservableProperty]
    private double startTimeSeconds;

    [ObservableProperty]
    private double durationSeconds = 2.0;

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
        ModelCardViewModel.ApplyStep(applyArgs);

        var video =
            SelectVideoCardViewModel.LoadVideoNode(applyArgs)
            ?? throw new ValidationException("Select a source video for Retake");

        var sliced = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.VideoSlice
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.VideoSlice)),
                Video = video,
                StartTime = StartTimeSeconds,
                Duration = DurationSeconds,
                StrictDuration = false,
            }
        );

        var components = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.GetVideoComponents
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.GetVideoComponents)),
                Video = sliced.Output,
            }
        );

        builder.Connections.Primary = components.Output1;
        builder.Connections.LtxPassthroughAudio = components.Output2;
        if (VideoOutputSettingsCardViewModel.AddAudio)
        {
            builder.Connections.UseLtxNativeAudio = true;
            // Prefer re-using sliced audio as passthrough; still generate video with native AV if enabled
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

        SamplerCardViewModel.IsDenoiseStrengthEnabled = true;
        SamplerCardViewModel.ApplyStep(applyArgs);
        applyArgs.InvokeAllPreOutputActions();
        VideoOutputSettingsCardViewModel.ApplyStep(applyArgs);
    }
}
