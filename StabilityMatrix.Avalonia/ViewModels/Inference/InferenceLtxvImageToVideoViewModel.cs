using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceLtxvImageToVideoView), IsPersistent = true)]
[RegisterScoped<InferenceLtxvImageToVideoViewModel>, ManagedService]
public class InferenceLtxvImageToVideoViewModel : InferenceLtxvTextToVideoViewModel
{
    public InferenceLtxvImageToVideoViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        SelectImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>();
        SamplerCardViewModel.IsDenoiseStrengthEnabled = true;
        SamplerCardViewModel.DenoiseStrength = 1.0d;
    }

    [JsonPropertyName("ImageLoader")]
    public SelectImageCardViewModel SelectImageCardViewModel { get; }

    /// <inheritdoc />
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

        var imageLoad = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadImage
            {
                Name = builder.Nodes.GetUniqueName("LTXV_LoadImage"),
                Image =
                    SelectImageCardViewModel.ImageSource?.GetHashGuidFileNameCached("Inference")
                    ?? throw new ValidationException("No image selected"),
            }
        );
        builder.Connections.Primary = imageLoad.Output1;
        builder.Connections.PrimarySize = SelectImageCardViewModel.CurrentBitmapSize;

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

    /// <inheritdoc />
    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (SelectImageCardViewModel.ImageSource is { } image)
            yield return image;
    }
}
