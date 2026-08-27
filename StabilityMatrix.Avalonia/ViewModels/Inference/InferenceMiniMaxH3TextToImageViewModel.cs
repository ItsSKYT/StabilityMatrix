using Injectio.Attributes;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceMiniMaxH3TextToImageView), IsPersistent = true)]
[RegisterScoped<InferenceMiniMaxH3TextToImageViewModel>, ManagedService]
public class InferenceMiniMaxH3TextToImageViewModel : InferenceMiniMaxH3TextToVideoViewModel
{
    public InferenceMiniMaxH3TextToImageViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        SamplerCardViewModel.IsLengthEnabled = false;
        SamplerCardViewModel.Length = 5;
        SamplerCardViewModel.Steps = 20;
        VideoOutputSettingsCardViewModel.AddAudio = false;
        StackCardViewModel.Cards.Remove(VideoOutputSettingsCardViewModel);
    }

    protected override bool ImageOnly => true;
}
