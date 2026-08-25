using System.ComponentModel.DataAnnotations;
using System.Linq;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models;
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
    public LtxvAdvancedOptionsCardViewModel? AdvancedOptions { get; set; }

    /// <summary>Optional second keyframe guide (end frame).</summary>
    public ImageSource? ExtraGuideImage { get; set; }
    public int ExtraGuideFrameIdx { get; set; }

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

        var result = LtxvComfyPipeline.Sample(
            new LtxvComfyPipeline.SampleArgs
            {
                EventArgs = e,
                Model = model,
                Positive = positive,
                Negative = negative,
                VideoLatent = latent,
                VideoVae = e.Builder.Connections.GetDefaultVAE(),
                Sampler = primarySampler,
                Steps = Steps,
                Cfg = CfgScale,
                Seed = e.Builder.Connections.Seed,
                Length = Length,
                Fps = e.Builder.Connections.VideoOutputFps,
                BatchSize = e.Builder.Connections.BatchSize,
                UseNativeAudio = e.Builder.Connections.UseLtxNativeAudio,
                ForceKitchenEager = e.Builder.Connections.ForceKitchenEager,
                AudioVae = e.Builder.Connections.LtxAudioVae,
                EncodedAudioLatent = e.Builder.Connections.LtxEncodedAudioLatent,
                PassthroughAudio = e.Builder.Connections.LtxPassthroughAudio,
                Advanced = AdvancedOptions,
                ExtraGuideImage = ExtraGuideImage,
                ExtraGuideFrameIdx = ExtraGuideFrameIdx,
                UseLtx25 = e.Builder.Connections.UseLtx25,
            }
        );

        e.Builder.Connections.Primary = result.VideoLatent;
        e.Builder.Connections.LtxAudioLatent = result.AudioLatent;
        e.Builder.Connections.LtxPassthroughAudio = result.PassthroughAudio;
        e.Builder.Connections.VideoFrameCount = Length;
    }
}
