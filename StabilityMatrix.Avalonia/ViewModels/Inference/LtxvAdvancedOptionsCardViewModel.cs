using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(LtxvAdvancedOptionsCard))]
[ManagedService]
[RegisterTransient<LtxvAdvancedOptionsCardViewModel>]
public partial class LtxvAdvancedOptionsCardViewModel : LoadableViewModelBase
{
    [ObservableProperty]
    private bool enableTwoStage;

    [ObservableProperty]
    private string? spatialUpscalerName;

    [ObservableProperty]
    private string? distilledLoraName;

    [ObservableProperty]
    private double distilledLoraStrength = 0.8;

    [ObservableProperty]
    private int stage2Steps = 4;

    [ObservableProperty]
    private bool enableTemporalUpscale;

    [ObservableProperty]
    private string? temporalUpscalerName;

    [ObservableProperty]
    private bool enableReferenceAudio;

    [ObservableProperty]
    private double identityGuidanceScale = 3.0;

    [ObservableProperty]
    private bool enableGuideImage;

    [ObservableProperty]
    private int guideFrameIdx;

    [ObservableProperty]
    private double guideStrength = 1.0;

    [JsonIgnore]
    public SelectAudioCardViewModel ReferenceAudioCard { get; }

    [JsonIgnore]
    public SelectImageCardViewModel GuideImageCard { get; }

    [JsonIgnore]
    public IInferenceClientManager ClientManager { get; }

    public LtxvAdvancedOptionsCardViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager clientManager
    )
    {
        ClientManager = clientManager;
        ReferenceAudioCard = vmFactory.Get<SelectAudioCardViewModel>();
        GuideImageCard = vmFactory.Get<SelectImageCardViewModel>();
    }

    public bool ShowTwoStageSettings => EnableTwoStage;
    public bool ShowTemporalSettings => EnableTemporalUpscale;
    public bool ShowReferenceAudio => EnableReferenceAudio;
    public bool ShowGuideImage => EnableGuideImage;

    partial void OnEnableTwoStageChanged(bool value) => OnPropertyChanged(nameof(ShowTwoStageSettings));

    partial void OnEnableTemporalUpscaleChanged(bool value) =>
        OnPropertyChanged(nameof(ShowTemporalSettings));

    partial void OnEnableReferenceAudioChanged(bool value) => OnPropertyChanged(nameof(ShowReferenceAudio));

    partial void OnEnableGuideImageChanged(bool value) => OnPropertyChanged(nameof(ShowGuideImage));

    [RelayCommand]
    private void ApplyPortraitPreset(string preset)
    {
        // Handled by parent via message / PropertyChanged — sampler dims set by Inference VM
        PortraitPresetRequested?.Invoke(this, preset);
    }

    public event EventHandler<string>? PortraitPresetRequested;

    public string? ResolveSpatialUpscaler()
    {
        if (!string.IsNullOrWhiteSpace(SpatialUpscalerName))
            return SpatialUpscalerName;

        return ClientManager
            .AllModels.Concat(ClientManager.UnetModels)
            .Select(m => m.RelativePath)
            .FirstOrDefault(p =>
                p.Contains("ltx-2.5-latent-spatial-upscaler", StringComparison.OrdinalIgnoreCase)
                || p.Contains("latent-spatial-upscaler", StringComparison.OrdinalIgnoreCase)
                || p.Contains("spatial-upscaler-x2", StringComparison.OrdinalIgnoreCase)
                || p.Contains("ltx-2.3-spatial-upscaler", StringComparison.OrdinalIgnoreCase)
                || p.Contains("spatial-upscaler", StringComparison.OrdinalIgnoreCase)
                || p.Contains("spatial_upscaler", StringComparison.OrdinalIgnoreCase)
            );
    }

    public string? ResolveTemporalUpscaler()
    {
        if (!string.IsNullOrWhiteSpace(TemporalUpscalerName))
            return TemporalUpscalerName;

        return ClientManager
            .Models.Select(m => m.RelativePath)
            .FirstOrDefault(p =>
                p.Contains("temporal-upscaler", StringComparison.OrdinalIgnoreCase)
                || p.Contains("temporal_upscaler", StringComparison.OrdinalIgnoreCase)
            );
    }

    public string? ResolveDistilledLora()
    {
        if (!string.IsNullOrWhiteSpace(DistilledLoraName))
            return DistilledLoraName;

        return ClientManager
            .LoraModels.Select(m => m.RelativePath)
            .FirstOrDefault(p =>
                p.Contains("distilled-lora", StringComparison.OrdinalIgnoreCase)
                || p.Contains("distilled_lora", StringComparison.OrdinalIgnoreCase)
                || p.Contains("lora-384", StringComparison.OrdinalIgnoreCase)
            );
    }
}
