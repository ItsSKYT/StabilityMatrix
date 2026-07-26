using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Injectio.Attributes;
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

[View(typeof(InferenceLtxvKeyframeInterpView), IsPersistent = true)]
[RegisterScoped<InferenceLtxvKeyframeInterpViewModel>, ManagedService]
public class InferenceLtxvKeyframeInterpViewModel : InferenceLtxvTextToVideoViewModel
{
    public InferenceLtxvKeyframeInterpViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        KeyframeA = vmFactory.Get<SelectImageCardViewModel>();
        KeyframeB = vmFactory.Get<SelectImageCardViewModel>();
        StackCardViewModel.AddCards(KeyframeA, KeyframeB);
        AdvancedOptionsCardViewModel.EnableGuideImage = false;
    }

    [JsonPropertyName("KeyframeA")]
    public SelectImageCardViewModel KeyframeA { get; }

    [JsonPropertyName("KeyframeB")]
    public SelectImageCardViewModel KeyframeB { get; }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        if (KeyframeA.ImageSource is null || KeyframeB.ImageSource is null)
            throw new ValidationException("Select two keyframe images (start and end)");

        AdvancedOptionsCardViewModel.EnableGuideImage = true;
        AdvancedOptionsCardViewModel.GuideImageCard.ImageSource = KeyframeA.ImageSource;
        AdvancedOptionsCardViewModel.GuideFrameIdx = 0;
        AdvancedOptionsCardViewModel.GuideStrength = 1.0;
        SamplerCardViewModel.ExtraGuideImage = KeyframeB.ImageSource;
        SamplerCardViewModel.ExtraGuideFrameIdx = Math.Max(0, SamplerCardViewModel.Length - 1);

        base.BuildPrompt(args);
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (KeyframeA.ImageSource is { } a)
            yield return a;
        if (KeyframeB.ImageSource is { } b)
            yield return b;
    }
}
