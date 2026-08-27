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

[View(typeof(InferenceMiniMaxH3VideoToVideoView), IsPersistent = true)]
[RegisterScoped<InferenceMiniMaxH3VideoToVideoViewModel>, ManagedService]
public class InferenceMiniMaxH3VideoToVideoViewModel : InferenceMiniMaxH3TextToVideoViewModel
{
    public InferenceMiniMaxH3VideoToVideoViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        ModelCardViewModel.UseRef2Va = true;
        SelectVideoCardViewModel = vmFactory.Get<SelectVideoCardViewModel>();
        StackCardViewModel.AddCards(SelectVideoCardViewModel);
    }

    [JsonPropertyName("VideoLoader")]
    public SelectVideoCardViewModel SelectVideoCardViewModel { get; }

    protected override (ImageNodeConnection? Frames, AudioNodeConnection? Audio) LoadRefVideo(
        ModuleApplyStepEventArgs e
    )
    {
        var video =
            SelectVideoCardViewModel.LoadVideoNode(e)
            ?? throw new ValidationException("Select a reference video");
        var parts = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.GetVideoComponents
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.GetVideoComponents)),
                Video = video,
            }
        );
        return (parts.Output1, parts.Output2);
    }
}
