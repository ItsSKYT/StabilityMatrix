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

[View(typeof(InferenceMiniMaxH3ImageToVideoView), IsPersistent = true)]
[RegisterScoped<InferenceMiniMaxH3ImageToVideoViewModel>, ManagedService]
public class InferenceMiniMaxH3ImageToVideoViewModel : InferenceMiniMaxH3TextToVideoViewModel
{
    public InferenceMiniMaxH3ImageToVideoViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        SelectImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>();
    }

    [JsonPropertyName("ImageLoader")]
    public SelectImageCardViewModel SelectImageCardViewModel { get; }

    protected override ImageNodeConnection? LoadFirstFrame(ModuleApplyStepEventArgs e)
    {
        var image =
            SelectImageCardViewModel.ImageSource
            ?? throw new ValidationException("No image selected");
        return e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadImage
                {
                    Name = e.Nodes.GetUniqueName("H3_LoadImage"),
                    Image = image.GetHashGuidFileNameCached("Inference").Replace('\\', '/'),
                }
            )
            .Output1;
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (SelectImageCardViewModel.ImageSource is { } image)
            yield return image;
    }
}
