using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Languages;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Dialogs;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

/// <summary>
/// Model card for LTXV 2.3 (transformer-only packs): Checkpoint/UNET + DualCLIP (Gemma + text projection) + VAE.
/// </summary>
[View(typeof(LtxvModelCard))]
[ManagedService]
[RegisterTransient<LtxvModelCardViewModel>]
public partial class LtxvModelCardViewModel(
    IInferenceClientManager clientManager,
    IServiceManager<ViewModelBase> vmFactory
) : LoadableViewModelBase, IParametersLoadableState, IComfyStep
{
    [ObservableProperty]
    private HybridModelFile? selectedModel;

    [ObservableProperty]
    private HybridModelFile? selectedClipModel;

    [ObservableProperty]
    private HybridModelFile? selectedTextProjection;

    [ObservableProperty]
    private HybridModelFile? selectedVae;

    [ObservableProperty]
    private double maxShift = 2.05d;

    [ObservableProperty]
    private double baseShift = 0.95d;

    public IInferenceClientManager ClientManager { get; } = clientManager;

    public StackEditableCardViewModel ExtraNetworksStackCardViewModel { get; } =
        new(vmFactory) { Title = Resources.Label_ExtraNetworks, AvailableModules = [typeof(LoraModule)] };

    [RelayCommand]
    private async Task OpenModelPickerAsync()
    {
        using var pickerScope = vmFactory.CreateScope();
        var pickerVm = pickerScope.ServiceManager.Get<ModelPickerDialogViewModel>();
        pickerVm.Title = "Select LTXV Checkpoint / UNET";
        pickerVm.Source = ModelPickerSource.CheckpointAndUnet;

        if (await pickerVm.GetDialog().ShowAsync() == ContentDialogResult.Primary
            && pickerVm.SelectedModel is { } selected)
        {
            SelectedModel = selected;
        }
    }

    [RelayCommand]
    private async Task OpenClipPickerAsync()
    {
        using var pickerScope = vmFactory.CreateScope();
        var pickerVm = pickerScope.ServiceManager.Get<ModelPickerDialogViewModel>();
        pickerVm.Title = "Select Gemma Text Encoder";
        pickerVm.Source = ModelPickerSource.Clip;

        if (await pickerVm.GetDialog().ShowAsync() == ContentDialogResult.Primary
            && pickerVm.SelectedModel is { } selected)
        {
            SelectedClipModel = selected;
        }
    }

    [RelayCommand]
    private async Task OpenTextProjectionPickerAsync()
    {
        using var pickerScope = vmFactory.CreateScope();
        var pickerVm = pickerScope.ServiceManager.Get<ModelPickerDialogViewModel>();
        pickerVm.Title = "Select LTX Text Projection";
        pickerVm.Source = ModelPickerSource.Clip;

        if (await pickerVm.GetDialog().ShowAsync() == ContentDialogResult.Primary
            && pickerVm.SelectedModel is { } selected)
        {
            SelectedTextProjection = selected;
        }
    }

    [RelayCommand]
    private async Task OpenVaePickerAsync()
    {
        using var pickerScope = vmFactory.CreateScope();
        var pickerVm = pickerScope.ServiceManager.Get<ModelPickerDialogViewModel>();
        pickerVm.Title = "Select LTX VAE (taeltx2_3 / LTX23_video_vae)";
        pickerVm.Source = ModelPickerSource.Vae;

        if (await pickerVm.GetDialog().ShowAsync() == ContentDialogResult.Primary
            && pickerVm.SelectedModel is { } selected)
        {
            SelectedVae = selected;
        }
    }

    public async Task<bool> ValidateModel()
    {
        if (SelectedModel is null)
        {
            await DialogHelper
                .CreateMarkdownDialog("Please select an LTXV checkpoint / UNET.", "No Model Selected")
                .ShowAsync();
            return false;
        }

        if (SelectedClipModel is null)
        {
            await DialogHelper
                .CreateMarkdownDialog(
                    "Please select Gemma 3 12B text encoder (`gemma_3_12B_it_fp8_scaled`).",
                    "No Text Encoder Selected"
                )
                .ShowAsync();
            return false;
        }

        if (ResolveTextProjection() is null)
        {
            await DialogHelper
                .CreateMarkdownDialog(
                    "Please select LTX text projection (`ltx-2.3_text_projection_bf16.safetensors`) in TextEncoders.",
                    "No Text Projection Selected"
                )
                .ShowAsync();
            return false;
        }

        if (ResolveVae() is null)
        {
            await DialogHelper
                .CreateMarkdownDialog(
                    "Please select LTX VAE (`taeltx2_3.safetensors` or `LTX23_video_vae_bf16.safetensors`).",
                    "No VAE Selected"
                )
                .ShowAsync();
            return false;
        }

        return true;
    }

    public bool IsLikelyConvRotInt4Model()
    {
        var name = SelectedModel?.FileName ?? SelectedModel?.RelativePath ?? string.Empty;
        return name.Contains("convrot", StringComparison.OrdinalIgnoreCase)
            || name.Contains("conv_rot", StringComparison.OrdinalIgnoreCase)
            || (
                name.Contains("int4", StringComparison.OrdinalIgnoreCase)
                && (
                    name.Contains("w4a4", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("ltx", StringComparison.OrdinalIgnoreCase)
                )
            );
    }

    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        var modelPath =
            SelectedModel?.RelativePath ?? throw new ValidationException("Model not selected");
        var textEncoder =
            SelectedClipModel?.RelativePath
            ?? throw new ValidationException("No Gemma text encoder selected");
        var textProjection =
            ResolveTextProjection()?.RelativePath
            ?? throw new ValidationException("No LTX text projection selected");
        var vaePath =
            ResolveVae()?.RelativePath ?? throw new ValidationException("No LTX VAE selected");

        ModelNodeConnection loadedModel;
        if (
            modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            || SelectedModel?.Local?.SharedFolderType == SharedFolderType.DiffusionModels
            || ClientManager.UnetModels.Any(m =>
                string.Equals(m.RelativePath, modelPath, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            // Prefer UNET path for transformer-only / GGUF packs
            if (modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                loadedModel = e
                    .Nodes.AddTypedNode(
                        new ComfyNodeBuilder.UnetLoaderGGUF
                        {
                            Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.UnetLoaderGGUF)),
                            UnetName = modelPath,
                        }
                    )
                    .Output;
            }
            else
            {
                loadedModel = e
                    .Nodes.AddTypedNode(
                        new ComfyNodeBuilder.UNETLoader
                        {
                            Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.UNETLoader)),
                            UnetName = modelPath,
                            WeightDtype = "default",
                        }
                    )
                    .Output;
            }
        }
        else
        {
            loadedModel = e
                .Nodes.AddTypedNode(
                    new ComfyNodeBuilder.CheckpointLoaderSimple
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.CheckpointLoaderSimple)),
                        CkptName = modelPath,
                    }
                )
                .Output1;
        }

        var clip = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.DualCLIPLoader
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.DualCLIPLoader)),
                ClipName1 = textEncoder,
                ClipName2 = textProjection,
                Type = "ltxv",
            }
        );

        var model = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.ModelSamplingLTXV
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.ModelSamplingLTXV)),
                Model = loadedModel,
                MaxShift = MaxShift,
                BaseShift = BaseShift,
            }
        );

        var vae = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.VAELoader
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.VAELoader)),
                VaeName = vaePath,
            }
        );

        e.Builder.Connections.Base.Model = model.Output;
        e.Builder.Connections.Base.Clip = clip.Output;
        e.Builder.Connections.Base.VAE = vae.Output;
        e.Builder.Connections.PrimaryVAE = vae.Output;

        if (IsLikelyConvRotInt4Model())
            e.Builder.Connections.LikelyConvRotInt4 = true;

        // INT4 ConvRot + joint LTXAV: CUDA kitchen kernel often aborts — force eager path.
        if (e.Builder.Connections.UseLtxNativeAudio && e.Builder.Connections.LikelyConvRotInt4)
            e.Builder.Connections.ForceKitchenEager = true;

        if (e.Builder.Connections.UseLtxNativeAudio)
        {
            var audioVaeName =
                ResolveAudioVaeCkptName(modelPath)
                ?? throw new ValidationException(
                    "LTX native audio needs LTX23_audio_vae_bf16.safetensors in Checkpoints "
                        + "(ComfyUI LTXVAudioVAELoader), or a full LTX checkpoint."
                );

            var audioVae = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LTXVAudioVAELoader
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVAudioVAELoader)),
                    CkptName = audioVaeName,
                }
            );
            e.Builder.Connections.LtxAudioVae = audioVae.Output;
        }

        if (ExtraNetworksStackCardViewModel.Cards.OfType<LoraModule>().Any(x => x.IsEnabled))
        {
            ExtraNetworksStackCardViewModel.ApplyStep(e);
        }
    }

    private string? ResolveAudioVaeCkptName(string selectedModelPath)
    {
        static bool IsAudioVae(string name) =>
            name.Contains("audio_vae", StringComparison.OrdinalIgnoreCase)
            || (
                name.Contains("ltx", StringComparison.OrdinalIgnoreCase)
                && name.Contains("audio", StringComparison.OrdinalIgnoreCase)
                && name.Contains("vae", StringComparison.OrdinalIgnoreCase)
            );

        // Dedicated audio VAE must live under Checkpoints for LTXVAudioVAELoader
        var dedicated = ClientManager.Models.FirstOrDefault(m => IsAudioVae(m.FileName));
        if (dedicated?.RelativePath is { } path)
            return path;

        // Full LTX checkpoint often embeds audio VAE metadata (community workaround)
        if (
            !selectedModelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && !ClientManager.UnetModels.Any(m =>
                string.Equals(m.RelativePath, selectedModelPath, StringComparison.OrdinalIgnoreCase)
            )
            && selectedModelPath.Contains("ltx", StringComparison.OrdinalIgnoreCase)
        )
        {
            return selectedModelPath;
        }

        return null;
    }

    private HybridModelFile? ResolveTextProjection()
    {
        if (SelectedTextProjection is { } selected)
            return selected;

        return ClientManager.ClipModels.FirstOrDefault(m =>
            m.FileName.Contains("text_projection", StringComparison.OrdinalIgnoreCase)
            || m.FileName.Contains("ltx-2.3_text_projection", StringComparison.OrdinalIgnoreCase)
        );
    }

    private HybridModelFile? ResolveVae()
    {
        if (SelectedVae is { } selected)
            return selected;

        return ClientManager.VaeModels.FirstOrDefault(m =>
                m.FileName.Contains("taeltx2_3", StringComparison.OrdinalIgnoreCase)
            )
            ?? ClientManager.VaeModels.FirstOrDefault(m =>
                m.FileName.Contains("LTX23_video_vae", StringComparison.OrdinalIgnoreCase)
                || m.FileName.Contains("ltx", StringComparison.OrdinalIgnoreCase)
                    && m.FileName.Contains("vae", StringComparison.OrdinalIgnoreCase)
            );
    }

    public override void LoadStateFromJsonObject(JsonObject state)
    {
        base.LoadStateFromJsonObject(state);
        RematchSelections();
    }

    private void RematchSelections()
    {
        if (SelectedModel is { } model)
        {
            SelectedModel =
                ClientManager.Models.FirstOrDefault(m =>
                    string.Equals(
                        m.Local?.RelativePath,
                        model.Local?.RelativePath,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                ?? ClientManager.UnetModels.FirstOrDefault(m =>
                    string.Equals(
                        m.Local?.RelativePath,
                        model.Local?.RelativePath,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                ?? model;
        }

        if (SelectedClipModel is { } clip)
        {
            SelectedClipModel =
                ClientManager.ClipModels.FirstOrDefault(m =>
                    string.Equals(
                        m.Local?.RelativePath,
                        clip.Local?.RelativePath,
                        StringComparison.OrdinalIgnoreCase
                    )
                ) ?? clip;
        }

        if (SelectedTextProjection is { } projection)
        {
            SelectedTextProjection =
                ClientManager.ClipModels.FirstOrDefault(m =>
                    string.Equals(
                        m.Local?.RelativePath,
                        projection.Local?.RelativePath,
                        StringComparison.OrdinalIgnoreCase
                    )
                ) ?? projection;
        }

        if (SelectedVae is { } vae)
        {
            SelectedVae =
                ClientManager.VaeModels.FirstOrDefault(m =>
                    string.Equals(
                        m.Local?.RelativePath,
                        vae.Local?.RelativePath,
                        StringComparison.OrdinalIgnoreCase
                    )
                ) ?? vae;
        }
    }

    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        if (parameters.ModelName is not { } name)
            return;

        SelectedModel =
            ClientManager.Models.FirstOrDefault(m => m.RelativePath.EndsWith(name))
            ?? ClientManager.UnetModels.FirstOrDefault(m => m.RelativePath.EndsWith(name));
    }

    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        return parameters with { ModelName = SelectedModel?.FileName };
    }
}
