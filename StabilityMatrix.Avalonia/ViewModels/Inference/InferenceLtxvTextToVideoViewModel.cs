using System.Text.Json.Serialization;
using DesktopNotifications;
using FluentAvalonia.UI.Controls;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Video;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Models.Notifications;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;
using Notification = DesktopNotifications.Notification;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceLtxvTextToVideoView), IsPersistent = true)]
[RegisterScoped<InferenceLtxvTextToVideoViewModel>, ManagedService]
public class InferenceLtxvTextToVideoViewModel : InferenceGenerationViewModelBase, IParametersLoadableState
{
    private readonly INotificationService notificationService;

    [JsonIgnore]
    public StackCardViewModel StackCardViewModel { get; }

    [JsonPropertyName("Model")]
    public LtxvModelCardViewModel ModelCardViewModel { get; }

    [JsonPropertyName("Sampler")]
    public LtxvSamplerCardViewModel SamplerCardViewModel { get; }

    [JsonPropertyName("BatchSize")]
    public BatchSizeCardViewModel BatchSizeCardViewModel { get; }

    [JsonPropertyName("Seed")]
    public SeedCardViewModel SeedCardViewModel { get; }

    [JsonPropertyName("Prompt")]
    public PromptCardViewModel PromptCardViewModel { get; }

    [JsonPropertyName("VideoOutput")]
    public VideoOutputSettingsCardViewModel VideoOutputSettingsCardViewModel { get; }

    [JsonPropertyName("Advanced")]
    public LtxvAdvancedOptionsCardViewModel AdvancedOptionsCardViewModel { get; }

    public InferenceLtxvTextToVideoViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        this.notificationService = notificationService;
        SeedCardViewModel = vmFactory.Get<SeedCardViewModel>();
        SeedCardViewModel.GenerateNewSeed();

        ModelCardViewModel = vmFactory.Get<LtxvModelCardViewModel>();

        SamplerCardViewModel = vmFactory.Get<LtxvSamplerCardViewModel>(samplerCard =>
        {
            samplerCard.IsDimensionsEnabled = true;
            samplerCard.IsCfgScaleEnabled = true;
            samplerCard.IsSamplerSelectionEnabled = true;
            samplerCard.IsSchedulerSelectionEnabled = false;
            samplerCard.IsDenoiseStrengthEnabled = false;
            samplerCard.DenoiseStrength = 1.0d;
            samplerCard.EnableAddons = false;
            samplerCard.IsLengthEnabled = true;
            samplerCard.Width = 768;
            samplerCard.Height = 512;
            samplerCard.Length = 25;
            samplerCard.Steps = 8;
            samplerCard.CfgScale = 1.0d;
        });

        PromptCardViewModel = AddDisposable(vmFactory.Get<PromptCardViewModel>());
        BatchSizeCardViewModel = vmFactory.Get<BatchSizeCardViewModel>();
        VideoOutputSettingsCardViewModel = vmFactory.Get<VideoOutputSettingsCardViewModel>(vm =>
        {
            vm.Fps = 5.0d;
            vm.SupportsLtxNativeAudio = true;
            vm.SelectedAudioSource = VideoAudioSource.Ltx;
        });

        AdvancedOptionsCardViewModel = vmFactory.Get<LtxvAdvancedOptionsCardViewModel>();
        SamplerCardViewModel.AdvancedOptions = AdvancedOptionsCardViewModel;
        AdvancedOptionsCardViewModel.PortraitPresetRequested += (_, preset) =>
        {
            switch (preset)
            {
                case "landscape":
                    SamplerCardViewModel.Width = 768;
                    SamplerCardViewModel.Height = 512;
                    break;
                case "portrait":
                    SamplerCardViewModel.Width = 512;
                    SamplerCardViewModel.Height = 768;
                    break;
                case "portrait1080":
                    SamplerCardViewModel.Width = 1080;
                    SamplerCardViewModel.Height = 1920;
                    break;
            }
        };

        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
        StackCardViewModel.AddCards(
            ModelCardViewModel,
            SamplerCardViewModel,
            AdvancedOptionsCardViewModel,
            SeedCardViewModel,
            BatchSizeCardViewModel,
            VideoOutputSettingsCardViewModel
        );
    }

    /// <inheritdoc />
    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);

        var applyArgs = args.ToModuleApplyStepEventArgs();
        var builder = args.Builder;

        builder.Connections.Seed = args.SeedOverride switch
        {
            { } seed => Convert.ToUInt64(seed),
            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
        };

        VideoOutputSettingsCardViewModel.ApplyEarlyConnections(builder);

        ModelCardViewModel.ApplyStep(applyArgs);

        builder.SetupEmptyLatentSource(
            SamplerCardViewModel.Width,
            SamplerCardViewModel.Height,
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
        var frameRate = VideoOutputSettingsCardViewModel.Fps;
        var ltxCond = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LTXVConditioning
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LTXVConditioning)),
                Positive = conditioning.Positive,
                Negative = conditioning.Negative,
                FrameRate = frameRate,
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

    /// <inheritdoc />
    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        if (!await CheckClientConnectedWithPrompt() || !ClientManager.IsConnected)
            return;

        if (!await ModelCardViewModel.ValidateModel())
            return;

        if (
            !await EnsureSafeAudioPathForQuantizedModelAsync()
        )
            return;

        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();
        if (overrides is not { UseCurrentSeed: true } && seedCard.IsRandomizeEnabled)
            seedCard.GenerateNewSeed();

        var batches = BatchSizeCardViewModel.BatchCount;
        var batchArgs = new List<ImageGenerationEventArgs>();

        for (var i = 0; i < batches; i++)
        {
            var seed = seedCard.Seed + i;
            var buildPromptArgs = new BuildPromptEventArgs { Overrides = overrides, SeedOverride = seed };
            BuildPrompt(buildPromptArgs);

            var inferenceProject = InferenceProjectDocument.FromLoadable(this);
            if (inferenceProject.State?["Seed"]?["Seed"] is not null)
                inferenceProject = inferenceProject.WithState(x => x["Seed"]["Seed"] = seed);

            batchArgs.Add(
                new ImageGenerationEventArgs
                {
                    Client = ClientManager.Client!,
                    Nodes = buildPromptArgs.Builder.ToNodeDictionary(),
                    OutputNodeNames = buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray(),
                    Parameters = SaveStateToParameters(new GenerationParameters()) with
                    {
                        Seed = Convert.ToUInt64(seed),
                    },
                    Project = inferenceProject,
                    FilesToTransfer = buildPromptArgs.FilesToTransfer,
                    BatchIndex = i,
                    ClearOutputImages = i == 0,
                }
            );
        }

        foreach (var args in batchArgs)
            await RunGeneration(args, cancellationToken);

        if (batches > 1)
        {
            await notificationService.ShowAsync(
                NotificationKey.Inference_BatchCompleted,
                new Notification
                {
                    Title = "Batch Completed",
                    Body =
                        $"Batch of {batches} items [{Guid.NewGuid().ToString()[..7].ToLower()}] completed successfully",
                    BodyImagePath = ImageGalleryCardViewModel
                        .ImageSources.LastOrDefault()
                        ?.LocalFile?.FullPath,
                }
            );
        }
    }

    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        SamplerCardViewModel.LoadStateFromParameters(parameters);
        ModelCardViewModel.LoadStateFromParameters(parameters);
        PromptCardViewModel.LoadStateFromParameters(parameters);
        VideoOutputSettingsCardViewModel.LoadStateFromParameters(parameters);
        SeedCardViewModel.Seed = Convert.ToInt64(parameters.Seed);
    }

    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        parameters = SamplerCardViewModel.SaveStateToParameters(parameters);
        parameters = ModelCardViewModel.SaveStateToParameters(parameters);
        parameters = PromptCardViewModel.SaveStateToParameters(parameters);
        parameters = VideoOutputSettingsCardViewModel.SaveStateToParameters(parameters);
        parameters.Seed = (ulong)SeedCardViewModel.Seed;
        return parameters;
    }

    /// <summary>
    /// INT4 ConvRot + joint LTXAV: CUDA convrot_w4a4_linear often Fatal-Aborts.
    /// We auto-force the kitchen eager backend; offer MMAudio as an alternative.
    /// </summary>
    protected async Task<bool> EnsureSafeAudioPathForQuantizedModelAsync()
    {
        var useLtxAudio =
            VideoOutputSettingsCardViewModel.AddAudio
            && VideoOutputSettingsCardViewModel.SupportsLtxNativeAudio
            && VideoOutputSettingsCardViewModel.SelectedAudioSource == VideoAudioSource.Ltx;

        if (!useLtxAudio || !ModelCardViewModel.IsLikelyConvRotInt4Model())
            return true;

        var dialog = DialogHelper.CreateMarkdownDialog(
            "Your model looks like **INT4 ConvRot**.\n\n"
                + "Native **LTX audio** (joint `LTXAV`) often **hard-crashes** ComfyUI inside the CUDA "
                + "`convrot_w4a4_linear` kernel.\n\n"
                + "Stability Matrix will **stabilize** this run by forcing the **eager (PyTorch)** "
                + "comfy-kitchen backend (slower, but usually avoids the abort).\n\n"
                + "**Primary** = continue with LTX audio (stable/eager).\n"
                + "**Secondary** = switch Audio Source to **MMAudio** (faster Foley path).\n"
                + "Cancel = abort. Restart ComfyUI later to restore kitchen CUDA speed.",
            "INT4 ConvRot + LTX audio"
        );
        dialog.IsPrimaryButtonEnabled = true;
        dialog.PrimaryButtonText = "Continue (stable)";
        dialog.SecondaryButtonText = "Use MMAudio";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            return true;

        if (result == ContentDialogResult.Secondary)
        {
            VideoOutputSettingsCardViewModel.SelectedAudioSource = VideoAudioSource.MMAudio;
            return true;
        }

        return false;
    }
}
