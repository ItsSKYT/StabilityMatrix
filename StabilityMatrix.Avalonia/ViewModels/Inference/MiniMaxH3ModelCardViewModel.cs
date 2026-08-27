using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Dialogs;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(MiniMaxH3ModelCard))]
[ManagedService]
[RegisterTransient<MiniMaxH3ModelCardViewModel>]
public partial class MiniMaxH3ModelCardViewModel(
    IInferenceClientManager clientManager,
    IServiceManager<ViewModelBase> vmFactory
) : LoadableViewModelBase, IComfyStep
{
    [ObservableProperty]
    private HybridModelFile? selectedModel;

    [ObservableProperty]
    private HybridModelFile? selectedClipModel;

    [ObservableProperty]
    private HybridModelFile? selectedVideoVae;

    [ObservableProperty]
    private HybridModelFile? selectedAudioVae;

    [ObservableProperty]
    private HybridModelFile? selectedTurboLora;

    [ObservableProperty]
    private bool enableTurbo;

    /// <summary>V2V uses Ref2VA weights; T2I/T2V/I2V use FL2VA.</summary>
    [ObservableProperty]
    private bool useRef2Va;

    public IInferenceClientManager ClientManager { get; } = clientManager;

    [RelayCommand]
    private async Task OpenModelPickerAsync()
    {
        using var pickerScope = vmFactory.CreateScope();
        var pickerVm = pickerScope.ServiceManager.Get<ModelPickerDialogViewModel>();
        pickerVm.Title = "Select MiniMax H3 UNET";
        pickerVm.Source = ModelPickerSource.CheckpointAndUnet;
        pickerVm.ShowUnetsOnly = true;
        if (await pickerVm.GetDialog().ShowAsync() == ContentDialogResult.Primary && pickerVm.SelectedModel is { } selected)
            SelectedModel = selected;
    }

    public async Task<bool> ValidateModel()
    {
        if (ResolveUnet() is null)
        {
            await DialogHelper
                .CreateMarkdownDialog(
                    "Select a MiniMax H3 UNET (`minimax_h3_fl2va_*.safetensors` for T2I/T2V/I2V, `minimax_h3_ref2va_*.safetensors` for V2V).",
                    "No MiniMax H3 model"
                )
                .ShowAsync();
            return false;
        }

        if (ResolveClip() is null)
        {
            await DialogHelper
                .CreateMarkdownDialog(
                    "Select the H3 text encoder (`qwen3vl_*_minimax_h3*.safetensors`) in TextEncoders.",
                    "No MiniMax H3 CLIP"
                )
                .ShowAsync();
            return false;
        }

        if (ResolveVideoVae() is null || ResolveAudioVae() is null)
        {
            await DialogHelper
                .CreateMarkdownDialog(
                    "Need `minimax_h3_video_vae_*.safetensors` and `minimax_h3_audio_vae_*.safetensors` in VAE.",
                    "No MiniMax H3 VAE"
                )
                .ShowAsync();
            return false;
        }

        return true;
    }

    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        var unetPath = ResolveUnet() ?? throw new ValidationException("No MiniMax H3 UNET selected");
        var clipPath = ResolveClip() ?? throw new ValidationException("No MiniMax H3 CLIP selected");
        var videoVaePath = ResolveVideoVae() ?? throw new ValidationException("No MiniMax H3 video VAE");
        var audioVaePath = ResolveAudioVae() ?? throw new ValidationException("No MiniMax H3 audio VAE");

        var model = e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.UNETLoader
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.UNETLoader)),
                    UnetName = unetPath,
                    WeightDtype = "default",
                }
            )
            .Output;

        if (
            unetPath.Contains("convrot", StringComparison.OrdinalIgnoreCase)
            || unetPath.Contains("int8", StringComparison.OrdinalIgnoreCase)
            || unetPath.Contains("int4", StringComparison.OrdinalIgnoreCase)
        )
        {
            model = e
                .Nodes.AddTypedNode(
                    new ComfyNodeBuilder.SM_KitchenForceEager
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.SM_KitchenForceEager)),
                        Model = model,
                    }
                )
                .Output;
            e.Builder.Connections.ForceKitchenEager = true;
        }

        if (EnableTurbo)
        {
            var lora =
                ResolveTurboLora()
                ?? throw new ValidationException(
                    UseRef2Va
                        ? "Turbo needs minimax_h3_ref2v_turbo_*.safetensors in Lora."
                        : "Turbo needs minimax_h3_fl2v_turbo_*.safetensors in Lora."
                );
            model = e.Nodes.AddNamedNode(
                ComfyNodeBuilder.LoraLoaderModelOnly(
                    e.Nodes.GetUniqueName("H3_TurboLora"),
                    model,
                    lora
                )
            ).Output;
        }

        var clip = SelectedClipModel is { IsGguf: true }
            ? e
                .Nodes.AddTypedNode(
                    new ComfyNodeBuilder.CLIPLoaderGGUF
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.CLIPLoaderGGUF)),
                        ClipName = clipPath,
                        Type = "minimax",
                    }
                )
                .Output
            : e
                .Nodes.AddTypedNode(
                    new ComfyNodeBuilder.CLIPLoader
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.CLIPLoader)),
                        ClipName = clipPath,
                        Type = "minimax",
                        Device = "default",
                    }
                )
                .Output;

        var videoVae = e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.VAELoader
                {
                    Name = e.Nodes.GetUniqueName("H3_VideoVAE"),
                    VaeName = videoVaePath,
                }
            )
            .Output;

        var audioVae = e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.VAELoader
                {
                    Name = e.Nodes.GetUniqueName("H3_AudioVAE"),
                    VaeName = audioVaePath,
                }
            )
            .Output;

        e.Builder.Connections.Base.Model = model;
        e.Builder.Connections.Base.Clip = clip;
        e.Builder.Connections.Base.VAE = videoVae;
        e.Builder.Connections.PrimaryVAE = videoVae;
        e.Builder.Connections.LtxAudioVae = audioVae;
    }

    public string? ResolveUnet()
    {
        if (!string.IsNullOrWhiteSpace(SelectedModel?.RelativePath))
            return SelectedModel.RelativePath;

        var models = ClientManager.UnetModels.Concat(ClientManager.Models);
        if (UseRef2Va)
        {
            return models
                .Select(m => m.RelativePath)
                .FirstOrDefault(p =>
                    p.Contains("minimax", StringComparison.OrdinalIgnoreCase)
                    && p.Contains("ref2va", StringComparison.OrdinalIgnoreCase)
                );
        }

        return models
            .Select(m => m.RelativePath)
            .FirstOrDefault(p =>
                p.Contains("minimax", StringComparison.OrdinalIgnoreCase)
                && p.Contains("fl2va", StringComparison.OrdinalIgnoreCase)
            );
    }

    public string? ResolveClip()
    {
        if (!string.IsNullOrWhiteSpace(SelectedClipModel?.RelativePath))
            return SelectedClipModel.RelativePath;

        return ClientManager
            .ClipModels.Select(m => m.RelativePath)
            .FirstOrDefault(p =>
                p.Contains("minimax", StringComparison.OrdinalIgnoreCase)
                && (
                    p.Contains("qwen3", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("qwen_3", StringComparison.OrdinalIgnoreCase)
                )
            );
    }

    public string? ResolveVideoVae()
    {
        if (!string.IsNullOrWhiteSpace(SelectedVideoVae?.RelativePath))
            return SelectedVideoVae.RelativePath;

        return ClientManager
            .VaeModels.Select(m => m.RelativePath)
            .FirstOrDefault(p =>
                p.Contains("minimax", StringComparison.OrdinalIgnoreCase)
                && p.Contains("video", StringComparison.OrdinalIgnoreCase)
                && p.Contains("vae", StringComparison.OrdinalIgnoreCase)
            );
    }

    public string? ResolveAudioVae()
    {
        if (!string.IsNullOrWhiteSpace(SelectedAudioVae?.RelativePath))
            return SelectedAudioVae.RelativePath;

        return ClientManager
            .VaeModels.Select(m => m.RelativePath)
            .FirstOrDefault(p =>
                p.Contains("minimax", StringComparison.OrdinalIgnoreCase)
                && p.Contains("audio", StringComparison.OrdinalIgnoreCase)
                && p.Contains("vae", StringComparison.OrdinalIgnoreCase)
            );
    }

    public string? ResolveTurboLora()
    {
        if (!string.IsNullOrWhiteSpace(SelectedTurboLora?.RelativePath))
            return SelectedTurboLora.RelativePath;

        var key = UseRef2Va ? "ref2v_turbo" : "fl2v_turbo";
        return ClientManager
            .LoraModels.Select(m => m.RelativePath)
            .FirstOrDefault(p =>
                p.Contains("minimax", StringComparison.OrdinalIgnoreCase)
                && p.Contains(key, StringComparison.OrdinalIgnoreCase)
            );
    }

    public override void LoadStateFromJsonObject(JsonObject state)
    {
        base.LoadStateFromJsonObject(state);
        SelectedModel = Rematch(ClientManager.UnetModels, SelectedModel);
        SelectedClipModel = Rematch(ClientManager.ClipModels, SelectedClipModel);
        SelectedVideoVae = Rematch(ClientManager.VaeModels, SelectedVideoVae);
        SelectedAudioVae = Rematch(ClientManager.VaeModels, SelectedAudioVae);
        SelectedTurboLora = Rematch(ClientManager.LoraModels, SelectedTurboLora);
    }

    private static HybridModelFile? Rematch(
        IEnumerable<HybridModelFile> models,
        HybridModelFile? selected
    )
    {
        if (selected is null)
            return null;
        var relativePath = selected.Local?.RelativePath;
        var fileName = selected.FileName;
        return models.FirstOrDefault(m =>
                relativePath is not null
                && string.Equals(m.Local?.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)
            )
            ?? models.FirstOrDefault(m =>
                !string.IsNullOrEmpty(fileName)
                && string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase)
            )
            ?? selected;
    }
}
