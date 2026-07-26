using System.ComponentModel.DataAnnotations;
using System.Linq;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(SamplerCard))]
[ManagedService]
[RegisterTransient<LtxvSamplerCardViewModel>]
public class LtxvSamplerCardViewModel : SamplerCardViewModel
{
    public LtxvSamplerCardViewModel(
        IInferenceClientManager clientManager,
        IServiceManager<ViewModelBase> vmFactory,
        ISettingsManager settingsManager,
        TabContext tabContext
    )
        : base(clientManager, vmFactory, settingsManager, tabContext)
    {
        EnableAddons = false;
        IsLengthEnabled = true;
        IsCfgScaleEnabled = true;
        IsSamplerSelectionEnabled = true;
        IsSchedulerSelectionEnabled = false;
        SelectedSampler = ComfySampler.Euler;
        SelectedScheduler = ComfyScheduler.Simple;
        Steps = 8;
        CfgScale = 1.0d;
        Width = 768;
        Height = 512;
        Length = 25;
        DenoiseStrength = 1.0d;
    }

    public override void ApplyStep(ModuleApplyStepEventArgs e)
    {
        if (EnableAddons)
        {
            foreach (var module in ModulesCardViewModel.Cards.OfType<ModuleBase>())
            {
                module.ApplyStep(e);
            }
        }

        var primarySampler = SelectedSampler ?? throw new ValidationException("Sampler not selected");
        e.Builder.Connections.PrimarySampler = primarySampler;
        e.Builder.Connections.PrimaryCfg = CfgScale;
        e.Builder.Connections.PrimarySteps = Steps;
        e.Temp = e.CreateTempFromBuilder();

        var conditioning = e.Temp.Base.Conditioning.Unwrap();
        var model = e.Temp.Base.Model!.Unwrap();
        var isImgToVid = IsDenoiseStrengthEnabled;

        LatentNodeConnection latent;
        var positive = conditioning.Positive;
        var negative = conditioning.Negative;

        if (isImgToVid)
        {
            var preprocess = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LTXVPreprocess
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVPreprocess)),
                    Image = e.Builder.GetPrimaryAsImage(),
                    ImgCompression = 18,
                }
            );

            var imgToVideo = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LTXVImgToVideo
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVImgToVideo)),
                    Positive = positive,
                    Negative = negative,
                    Vae = e.Builder.Connections.GetDefaultVAE(),
                    Image = preprocess.Output,
                    Width = Width,
                    Height = Height,
                    Length = Length,
                    BatchSize = e.Builder.Connections.BatchSize,
                    Strength = DenoiseStrength,
                }
            );

            positive = imgToVideo.Output1;
            negative = imgToVideo.Output2;
            latent = imgToVideo.Output3;
        }
        else
        {
            latent = e.Builder.GetPrimaryAsLatent(
                e.Temp.Primary!.Unwrap(),
                e.Builder.Connections.GetDefaultVAE()
            );
        }

        if (e.Builder.Connections.UseLtxNativeAudio)
            latent = ConcatEmptyAudioLatent(e, latent);

        var guider = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.CFGGuider
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.CFGGuider)),
                Model = model,
                Positive = positive,
                Negative = negative,
                Cfg = CfgScale,
            }
        );

        var scheduler = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVScheduler
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVScheduler)),
                Steps = Steps,
                MaxShift = 2.05,
                BaseShift = 0.95,
                Stretch = true,
                Terminal = 0.1,
                Latent = latent,
            }
        );

        var samplerSelect = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.KSamplerSelect
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.KSamplerSelect)),
                SamplerName = primarySampler.Name,
            }
        );

        var noise = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.RandomNoise
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.RandomNoise)),
                NoiseSeed = e.Builder.Connections.Seed,
            }
        );

        var sampler = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.SamplerCustomAdvanced
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.SamplerCustomAdvanced)),
                Noise = noise.Output,
                Guider = guider.Output,
                Sampler = samplerSelect.Output,
                Sigmas = scheduler.Output,
                LatentImage = latent,
            }
        );

        if (e.Builder.Connections.UseLtxNativeAudio)
        {
            var separated = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LTXVSeparateAVLatent
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVSeparateAVLatent)),
                    AvLatent = sampler.Output1,
                }
            );
            e.Builder.Connections.Primary = separated.Output1;
            e.Builder.Connections.LtxAudioLatent = separated.Output2;
        }
        else
        {
            e.Builder.Connections.Primary = sampler.Output1;
        }

        e.Builder.Connections.VideoFrameCount = Length;
    }

    private LatentNodeConnection ConcatEmptyAudioLatent(
        ModuleApplyStepEventArgs e,
        LatentNodeConnection videoLatent
    )
    {
        var audioVae =
            e.Builder.Connections.LtxAudioVae
            ?? throw new ValidationException(
                "LTX Audio VAE not loaded. Place LTX23_audio_vae_bf16.safetensors in Checkpoints."
            );

        var fps = e.Builder.Connections.VideoOutputFps;
        if (fps <= 0)
            fps = 24;

        var emptyAudio = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVEmptyLatentAudio
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVEmptyLatentAudio)),
                FramesNumber = Length,
                FrameRate = Math.Max(1, (int)Math.Round(fps)),
                BatchSize = e.Builder.Connections.BatchSize,
                AudioVae = audioVae,
            }
        );

        var concat = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVConcatAVLatent
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVConcatAVLatent)),
                VideoLatent = videoLatent,
                AudioLatent = emptyAudio.Output,
            }
        );

        return concat.Output;
    }
}
