using System.Text.Json.Serialization;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceLtxvTextToAudioView), IsPersistent = true)]
[RegisterScoped<InferenceLtxvTextToAudioViewModel>, ManagedService]
public class InferenceLtxvTextToAudioViewModel : InferenceLtxvTextToVideoViewModel
{
    public InferenceLtxvTextToAudioViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        VideoOutputSettingsCardViewModel.AddAudio = true;
        VideoOutputSettingsCardViewModel.SelectedAudioSource = VideoAudioSource.Ltx;
        VideoOutputSettingsCardViewModel.AudioOnlyMode = true;
        SamplerCardViewModel.Width = 64;
        SamplerCardViewModel.Height = 64;
        SamplerCardViewModel.Length = 97;
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        var applyArgs = args.ToModuleApplyStepEventArgs();
        var builder = args.Builder;

        builder.Connections.Seed = args.SeedOverride switch
        {
            { } seed => Convert.ToUInt64(seed),
            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
        };

        VideoOutputSettingsCardViewModel.AddAudio = true;
        VideoOutputSettingsCardViewModel.SelectedAudioSource = VideoAudioSource.Ltx;
        VideoOutputSettingsCardViewModel.AudioOnlyMode = true;
        VideoOutputSettingsCardViewModel.ApplyEarlyConnections(builder);
        builder.Connections.UseLtxNativeAudio = true;

        ModelCardViewModel.ApplyStep(applyArgs);

        var (latentW, latentH) = LtxvComfyPipeline.Stage1Size(
            SamplerCardViewModel.Width,
            SamplerCardViewModel.Height,
            ModelCardViewModel.IsLtx25
        );
        builder.SetupEmptyLatentSource(
            latentW,
            latentH,
            BatchSizeCardViewModel.BatchSize,
            BatchSizeCardViewModel.IsBatchIndexEnabled ? BatchSizeCardViewModel.BatchIndex : null,
            SamplerCardViewModel.Length,
            LatentType.Ltxv
        );

        BatchSizeCardViewModel.ApplyStep(applyArgs);
        PromptCardViewModel.ApplyStep(applyArgs);

        var conditioning =
            builder.Connections.Base.Conditioning
            ?? throw new InvalidOperationException("Conditioning not set");
        var ltxCond = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVConditioning
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVConditioning)),
                Positive = conditioning.Positive,
                Negative = conditioning.Negative,
                FrameRate = VideoOutputSettingsCardViewModel.Fps,
            }
        );
        builder.Connections.Base.Conditioning = new ConditioningConnections(
            ltxCond.Output1,
            ltxCond.Output2
        );

        SamplerCardViewModel.ApplyStep(applyArgs);
        applyArgs.InvokeAllPreOutputActions();
        VideoOutputSettingsCardViewModel.ApplyStep(applyArgs);
    }
}
