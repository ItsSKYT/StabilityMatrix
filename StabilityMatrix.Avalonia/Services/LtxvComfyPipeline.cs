using System.ComponentModel.DataAnnotations;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.ViewModels.Inference;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

namespace StabilityMatrix.Avalonia.Services;

/// <summary>
/// Shared LTX AV sample graph: optional guide/ref-audio, native audio latent, two-stage upscale.
/// </summary>
public static class LtxvComfyPipeline
{
    public sealed class SampleArgs
    {
        public required ModuleApplyStepEventArgs EventArgs { get; init; }
        public required ModelNodeConnection Model { get; init; }
        public required ConditioningNodeConnection Positive { get; init; }
        public required ConditioningNodeConnection Negative { get; init; }
        public required LatentNodeConnection VideoLatent { get; init; }
        public required VAENodeConnection VideoVae { get; init; }
        public required ComfySampler Sampler { get; init; }
        public required int Steps { get; init; }
        public required double Cfg { get; init; }
        public required ulong Seed { get; init; }
        public required int Length { get; init; }
        public required double Fps { get; init; }
        public int BatchSize { get; init; } = 1;
        public bool UseNativeAudio { get; init; }
        public bool ForceKitchenEager { get; init; }
        public VAENodeConnection? AudioVae { get; init; }
        public LatentNodeConnection? EncodedAudioLatent { get; init; }
        public AudioNodeConnection? PassthroughAudio { get; init; }
        public LtxvAdvancedOptionsCardViewModel? Advanced { get; init; }
        public ImageSource? ExtraGuideImage { get; init; }
        public int ExtraGuideFrameIdx { get; init; }
        public bool AudioOnly { get; init; }
        public bool UseLtx25 { get; init; }
        public ImageNodeConnection? InplaceImage { get; init; }
        public double InplaceStrength { get; init; } = 0.7;
    }

    public sealed class SampleResult
    {
        public required LatentNodeConnection VideoLatent { get; init; }
        public LatentNodeConnection? AudioLatent { get; init; }
        public AudioNodeConnection? PassthroughAudio { get; init; }
    }

    public static (int Width, int Height) Stage1Size(int width, int height, bool useLtx25)
    {
        if (!useLtx25)
            return (width, height);

        static int Align(int value) => Math.Max(64, value / 2 / 32 * 32);
        return (Align(width), Align(height));
    }

    public static SampleResult Sample(SampleArgs args)
    {
        var e = args.EventArgs;
        var model = args.Model;
        var positive = args.Positive;
        var negative = args.Negative;
        var latent = args.VideoLatent;
        var advanced = args.Advanced;

        var needsKitchenEager =
            args.ForceKitchenEager
            || e.Builder.Connections.ForceKitchenEager
            || (
                e.Builder.Connections.LikelyConvRotInt4
                && (args.UseNativeAudio || args.EncodedAudioLatent is not null || args.AudioOnly)
            );

        if (needsKitchenEager)
        {
            var eager = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.SM_KitchenForceEager
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.SM_KitchenForceEager)),
                    Model = model,
                }
            );
            model = eager.Output;
            e.Builder.Connections.ForceKitchenEager = true;
        }

        if (args.UseLtx25)
        {
            negative = e
                .Nodes.AddTypedNode(
                    new ComfyNodeBuilder.ConditioningZeroOut
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.ConditioningZeroOut)),
                        Conditioning = positive,
                    }
                )
                .Output;
        }

        if (advanced is { EnableGuideImage: true } && advanced.GuideImageCard.ImageSource is not null)
        {
            (positive, negative, latent) = ApplyGuide(
                e,
                positive,
                negative,
                latent,
                args.VideoVae,
                advanced.GuideImageCard.ImageSource,
                advanced.GuideFrameIdx,
                advanced.GuideStrength
            );
        }

        if (args.ExtraGuideImage is not null)
        {
            (positive, negative, latent) = ApplyGuide(
                e,
                positive,
                negative,
                latent,
                args.VideoVae,
                args.ExtraGuideImage,
                args.ExtraGuideFrameIdx,
                1.0
            );
        }

        if (
            advanced is { EnableReferenceAudio: true }
            && args.AudioVae is not null
            && advanced.ReferenceAudioCard.HasFile
        )
        {
            var refAudio =
                advanced.ReferenceAudioCard.LoadAudioNode(e)
                ?? throw new ValidationException("Reference audio failed to load");

            var refNode = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LTXVReferenceAudio
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVReferenceAudio)),
                    Model = model,
                    Positive = positive,
                    Negative = negative,
                    ReferenceAudio = refAudio,
                    AudioVae = args.AudioVae,
                    IdentityGuidanceScale = advanced.IdentityGuidanceScale,
                }
            );
            model = refNode.Output1;
            positive = refNode.Output2;
            negative = refNode.Output3;
        }

        LatentNodeConnection? audioLatent = null;
        var needsAudio = args.UseNativeAudio || args.EncodedAudioLatent is not null || args.AudioOnly;

        if (args.UseLtx25 && args.InplaceImage is not null)
        {
            latent = ApplyImgToVideoInplace(
                e,
                args.VideoVae,
                args.InplaceImage,
                latent,
                args.InplaceStrength,
                bypass: false
            );
        }

        if (needsAudio)
        {
            audioLatent = args.EncodedAudioLatent ?? CreateEmptyAudioLatent(e, args);
            latent = ConcatAv(e, latent, audioLatent);
        }

        var sampled = args.UseLtx25
            ? RunLtx25SamplerPass(
                e,
                model,
                positive,
                negative,
                latent,
                args.Cfg,
                args.Seed,
                Ltx25Stage1Sigmas
            )
            : RunSamplerPass(
                e,
                model,
                positive,
                negative,
                latent,
                args.Sampler,
                args.Steps,
                args.Cfg,
                args.Seed
            );

        LatentNodeConnection videoOut;
        LatentNodeConnection? audioOut = null;

        if (needsAudio)
        {
            var sep = Separate(e, sampled);
            videoOut = sep.video;
            audioOut = sep.audio;
        }
        else
        {
            videoOut = sampled;
        }

        var runTwoStage = advanced is { EnableTwoStage: true } || args.UseLtx25;

        if (runTwoStage && advanced is not null)
        {
            var (v2, a2) = args.UseLtx25
                ? RunLtx25TwoStage(e, model, positive, negative, videoOut, audioOut, args, advanced)
                : RunTwoStage(e, model, positive, negative, videoOut, audioOut, args, advanced);
            videoOut = v2;
            audioOut = a2;
        }
        else if (advanced is { EnableTemporalUpscale: true })
        {
            videoOut = ApplyLatentUpscale(
                e,
                videoOut,
                args.VideoVae,
                advanced.ResolveTemporalUpscaler()
                    ?? throw new ValidationException("Temporal upscaler model not found")
            );
        }

        return new SampleResult
        {
            VideoLatent = videoOut,
            AudioLatent = audioOut,
            PassthroughAudio = args.PassthroughAudio,
        };
    }

    private const string Ltx25Stage1Sigmas =
        "1.0, 0.99375, 0.9875, 0.98125, 0.975, 0.909375, 0.725, 0.421875, 0.0";

    private const string Ltx25Stage2Sigmas = "0.85, 0.7250, 0.4219, 0.0";

    private static (LatentNodeConnection video, LatentNodeConnection? audio) RunLtx25TwoStage(
        ModuleApplyStepEventArgs e,
        ModelNodeConnection model,
        ConditioningNodeConnection positive,
        ConditioningNodeConnection negative,
        LatentNodeConnection videoLatent,
        LatentNodeConnection? audioLatent,
        SampleArgs args,
        LtxvAdvancedOptionsCardViewModel advanced
    )
    {
        var spatialName =
            advanced.ResolveSpatialUpscaler() ?? "ltx-2.3-spatial-upscaler-x2-1.1.safetensors";

        var crop = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVCropGuides
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVCropGuides)),
                Positive = positive,
                Negative = negative,
                Latent = videoLatent,
            }
        );

        var upscaled = ApplyLatentUpscale(e, videoLatent, args.VideoVae, spatialName);

        if (advanced.EnableTemporalUpscale)
        {
            upscaled = ApplyLatentUpscale(
                e,
                upscaled,
                args.VideoVae,
                advanced.ResolveTemporalUpscaler()
                    ?? throw new ValidationException("Temporal upscaler model not found")
            );
        }

        if (args.InplaceImage is not null)
        {
            upscaled = ApplyImgToVideoInplace(
                e,
                args.VideoVae,
                args.InplaceImage,
                upscaled,
                strength: 1.0,
                bypass: false
            );
        }

        LatentNodeConnection stage2Latent = upscaled;
        if (audioLatent is not null)
            stage2Latent = ConcatAv(e, upscaled, audioLatent);

        var stage2 = RunLtx25SamplerPass(
            e,
            model,
            crop.Output1,
            crop.Output2,
            stage2Latent,
            args.Cfg,
            args.Seed + 1,
            Ltx25Stage2Sigmas
        );

        if (audioLatent is not null)
        {
            var sep = Separate(e, stage2);
            return (sep.video, sep.audio);
        }

        return (stage2, null);
    }

    private static (LatentNodeConnection video, LatentNodeConnection? audio) RunTwoStage(
        ModuleApplyStepEventArgs e,
        ModelNodeConnection model,
        ConditioningNodeConnection positive,
        ConditioningNodeConnection negative,
        LatentNodeConnection videoLatent,
        LatentNodeConnection? audioLatent,
        SampleArgs args,
        LtxvAdvancedOptionsCardViewModel advanced
    )
    {
        var spatialName =
            advanced.ResolveSpatialUpscaler()
            ?? throw new ValidationException(
                "Two-stage requires a spatial upscaler in latent_upscale_models / Checkpoints"
            );

        var upscaled = ApplyLatentUpscale(e, videoLatent, args.VideoVae, spatialName);

        if (advanced.EnableTemporalUpscale)
        {
            upscaled = ApplyLatentUpscale(
                e,
                upscaled,
                args.VideoVae,
                advanced.ResolveTemporalUpscaler()
                    ?? throw new ValidationException("Temporal upscaler model not found")
            );
        }

        var distilled =
            advanced.ResolveDistilledLora()
            ?? throw new ValidationException(
                "Two-stage requires distilled LoRA (e.g. ltx-2.3-*-distilled-lora-384)"
            );

        var loraModel = e.Nodes.AddNamedNode(
            ComfyNodeBuilder.LoraLoaderModelOnly(
                e.Nodes.GetUniqueName("LTXV_Stage2_Lora"),
                model,
                distilled,
                advanced.DistilledLoraStrength
            )
        );

        LatentNodeConnection stage2Latent = upscaled;
        if (audioLatent is not null)
            stage2Latent = ConcatAv(e, upscaled, audioLatent);

        var stage2 = RunSamplerPass(
            e,
            loraModel.Output,
            positive,
            negative,
            stage2Latent,
            args.Sampler,
            Math.Max(1, advanced.Stage2Steps),
            1.0,
            args.Seed + 1
        );

        if (audioLatent is not null)
        {
            var sep = Separate(e, stage2);
            return (sep.video, sep.audio);
        }

        return (stage2, null);
    }

    private static LatentNodeConnection ApplyImgToVideoInplace(
        ModuleApplyStepEventArgs e,
        VAENodeConnection vae,
        ImageNodeConnection image,
        LatentNodeConnection latent,
        double strength,
        bool bypass
    ) =>
        e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.LTXVImgToVideoInplace
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVImgToVideoInplace)),
                    Vae = vae,
                    Image = image,
                    Latent = latent,
                    Strength = strength,
                    Bypass = bypass,
                }
            )
            .Output;

    private static LatentNodeConnection ApplyLatentUpscale(
        ModuleApplyStepEventArgs e,
        LatentNodeConnection samples,
        VAENodeConnection vae,
        string modelName
    )
    {
        var loader = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LatentUpscaleModelLoader
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LatentUpscaleModelLoader)),
                ModelName = modelName,
            }
        );

        return e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.LTXVLatentUpsampler
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVLatentUpsampler)),
                    Samples = samples,
                    UpscaleModel = loader.Output,
                    Vae = vae,
                }
            )
            .Output;
    }

    private static (
        ConditioningNodeConnection positive,
        ConditioningNodeConnection negative,
        LatentNodeConnection latent
    ) ApplyGuide(
        ModuleApplyStepEventArgs e,
        ConditioningNodeConnection positive,
        ConditioningNodeConnection negative,
        LatentNodeConnection latent,
        VAENodeConnection vae,
        ImageSource image,
        int frameIdx,
        double strength
    )
    {
        var guidePath = image.GetHashGuidFileNameCached("Inference");
        e.AddFileTransfer(
            image.LocalFile?.FullPath ?? throw new ValidationException("Guide image has no local file"),
            guidePath
        );

        var loadGuide = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadImage
            {
                Name = e.Nodes.GetUniqueName("LTXV_GuideLoadImage"),
                Image = guidePath.Replace('\\', '/'),
            }
        );

        var guide = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVAddGuide
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVAddGuide)),
                Positive = positive,
                Negative = negative,
                Vae = vae,
                Latent = latent,
                Image = loadGuide.Output1,
                FrameIdx = frameIdx,
                Strength = strength,
            }
        );
        return (guide.Output1, guide.Output2, guide.Output3);
    }

    private static LatentNodeConnection CreateEmptyAudioLatent(ModuleApplyStepEventArgs e, SampleArgs args)
    {
        var audioVae =
            args.AudioVae
            ?? throw new ValidationException(
                "LTX Audio VAE required. Place LTX23_audio_vae_bf16.safetensors in Checkpoints."
            );

        var fps = args.Fps > 0 ? args.Fps : 24;
        return e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.LTXVEmptyLatentAudio
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVEmptyLatentAudio)),
                    FramesNumber = Math.Max(1, args.Length),
                    FrameRate = Math.Max(1, (int)Math.Round(fps)),
                    BatchSize = Math.Max(1, args.BatchSize),
                    AudioVae = audioVae,
                }
            )
            .Output;
    }

    private static LatentNodeConnection ConcatAv(
        ModuleApplyStepEventArgs e,
        LatentNodeConnection video,
        LatentNodeConnection audio
    ) =>
        e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.LTXVConcatAVLatent
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVConcatAVLatent)),
                    VideoLatent = video,
                    AudioLatent = audio,
                }
            )
            .Output;

    private static (LatentNodeConnection video, LatentNodeConnection audio) Separate(
        ModuleApplyStepEventArgs e,
        LatentNodeConnection av
    )
    {
        var sep = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVSeparateAVLatent
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVSeparateAVLatent)),
                AvLatent = av,
            }
        );
        return (sep.Output1, sep.Output2);
    }

    private static LatentNodeConnection RunLtx25SamplerPass(
        ModuleApplyStepEventArgs e,
        ModelNodeConnection model,
        ConditioningNodeConnection positive,
        ConditioningNodeConnection negative,
        LatentNodeConnection latent,
        double cfg,
        ulong seed,
        string sigmas
    )
    {
        var videoCfg = cfg > 0 ? cfg : 1.0;
        var guider = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.CFGGuider
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.CFGGuider)),
                Model = model,
                Positive = positive,
                Negative = negative,
                Cfg = videoCfg,
            }
        );

        var manualSigmas = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.ManualSigmas
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.ManualSigmas)),
                Sigmas = sigmas,
            }
        );

        var samplerSelect = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.KSamplerSelect
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.KSamplerSelect)),
                SamplerName = ComfySampler.Euler.Name,
            }
        );

        var noise = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.RandomNoise
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.RandomNoise)),
                NoiseSeed = seed,
            }
        );

        return e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.SamplerCustomAdvanced
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.SamplerCustomAdvanced)),
                    Noise = noise.Output,
                    Guider = guider.Output,
                    Sampler = samplerSelect.Output,
                    Sigmas = manualSigmas.Output,
                    LatentImage = latent,
                }
            )
            .Output1;
    }

    private static LatentNodeConnection RunSamplerPass(
        ModuleApplyStepEventArgs e,
        ModelNodeConnection model,
        ConditioningNodeConnection positive,
        ConditioningNodeConnection negative,
        LatentNodeConnection latent,
        ComfySampler sampler,
        int steps,
        double cfg,
        ulong seed
    )
    {
        var guider = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.CFGGuider
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.CFGGuider)),
                Model = model,
                Positive = positive,
                Negative = negative,
                Cfg = cfg,
            }
        );

        var scheduler = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVScheduler
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVScheduler)),
                Steps = steps,
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
                SamplerName = sampler.Name,
            }
        );

        var noise = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.RandomNoise
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.RandomNoise)),
                NoiseSeed = seed,
            }
        );

        return e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.SamplerCustomAdvanced
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.SamplerCustomAdvanced)),
                    Noise = noise.Output,
                    Guider = guider.Output,
                    Sampler = samplerSelect.Output,
                    Sigmas = scheduler.Output,
                    LatentImage = latent,
                }
            )
            .Output1;
    }
}
