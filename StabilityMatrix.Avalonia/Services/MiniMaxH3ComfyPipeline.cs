using System.ComponentModel.DataAnnotations;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

namespace StabilityMatrix.Avalonia.Services;

public static class MiniMaxH3ComfyPipeline
{
    /// <summary>H3 length grid is 17k+5 (5, 22, 39, 56, 73, …).</summary>
    public static int SnapLength(int frames)
    {
        var n = Math.Max(5, frames);
        var add = (5 - n % 17 + 17) % 17;
        return n + add;
    }

    public sealed class SampleArgs
    {
        public required ModuleApplyStepEventArgs EventArgs { get; init; }
        public required ModelNodeConnection Model { get; init; }
        public required ClipNodeConnection Clip { get; init; }
        public required VAENodeConnection VideoVae { get; init; }
        public required VAENodeConnection AudioVae { get; init; }
        public required string Prompt { get; init; }
        public required ComfySampler Sampler { get; init; }
        public required ComfyScheduler Scheduler { get; init; }
        public required int Steps { get; init; }
        public required ulong Seed { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Length { get; init; }
        public bool UseRef2Va { get; init; }
        public ImageNodeConnection? FirstFrame { get; init; }
        public ImageNodeConnection? LastFrame { get; init; }
        public ImageNodeConnection? RefVideoFrames { get; init; }
        public AudioNodeConnection? RefVideoAudio { get; init; }
        public bool ImageOnly { get; init; }
    }

    public static (ImageNodeConnection Images, AudioNodeConnection? Audio) Sample(SampleArgs args)
    {
        var e = args.EventArgs;
        var length = SnapLength(args.Length);
        var width = Math.Max(32, args.Width / 32 * 32);
        var height = Math.Max(32, args.Height / 32 * 32);

        ConditioningNodeConnection positive;
        LatentNodeConnection latent;

        if (args.UseRef2Va)
        {
            var refNode = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.MiniMaxH3ReferenceToVideo
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.MiniMaxH3ReferenceToVideo)),
                    Clip = args.Clip,
                    Vae = args.VideoVae,
                    AudioVae = args.AudioVae,
                    Prompt = args.Prompt,
                    Width = width,
                    Height = height,
                    Length = length,
                    RefImageSize = "match",
                    RefVideos = args.RefVideoFrames,
                    RefVideoAudios = args.RefVideoAudio,
                }
            );
            positive = refNode.Output1;
            latent = refNode.Output2;
        }
        else
        {
            var i2v = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.MiniMaxH3ImageToVideo
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.MiniMaxH3ImageToVideo)),
                    Clip = args.Clip,
                    Vae = args.VideoVae,
                    Prompt = args.Prompt,
                    Width = width,
                    Height = height,
                    Length = length,
                    FirstFrame = args.FirstFrame,
                    LastFrame = args.LastFrame,
                }
            );
            positive = i2v.Output1;
            latent = i2v.Output2;
        }

        var guider = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.BasicGuider
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.BasicGuider)),
                Model = args.Model,
                Conditioning = positive,
            }
        );

        var scheduler = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.BasicScheduler
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.BasicScheduler)),
                Model = args.Model,
                Scheduler = args.Scheduler.Name,
                Steps = Math.Max(1, args.Steps),
                Denoise = 1.0,
            }
        );

        var samplerSelect = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.KSamplerSelect
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.KSamplerSelect)),
                SamplerName = args.Sampler.Name,
            }
        );

        var noise = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.RandomNoise
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.RandomNoise)),
                NoiseSeed = args.Seed,
            }
        );

        var sampled = e
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

        var images = e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.VAEDecode
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.VAEDecode)),
                    Samples = sampled,
                    Vae = args.VideoVae,
                }
            )
            .Output;

        if (args.ImageOnly)
        {
            images = e
                .Nodes.AddTypedNode(
                    new ComfyNodeBuilder.ImageFromBatch
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.ImageFromBatch)),
                        Image = images,
                        BatchIndex = 0,
                        Length = 1,
                    }
                )
                .Output;
            return (images, null);
        }

        var audio = e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.VAEDecodeAudio
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.VAEDecodeAudio)),
                    Samples = sampled,
                    Vae = args.AudioVae,
                }
            )
            .Output;

        return (images, audio);
    }
}
