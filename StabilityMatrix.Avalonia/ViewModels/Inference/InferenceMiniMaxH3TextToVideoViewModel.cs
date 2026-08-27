using System.Text.Json.Serialization;
using DesktopNotifications;
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
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;
using Notification = DesktopNotifications.Notification;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceMiniMaxH3TextToVideoView), IsPersistent = true)]
[RegisterScoped<InferenceMiniMaxH3TextToVideoViewModel>, ManagedService]
public class InferenceMiniMaxH3TextToVideoViewModel
    : InferenceGenerationViewModelBase,
        IParametersLoadableState
{
    private readonly INotificationService notificationService;

    [JsonIgnore]
    public StackCardViewModel StackCardViewModel { get; }

    [JsonPropertyName("Model")]
    public MiniMaxH3ModelCardViewModel ModelCardViewModel { get; }

    [JsonPropertyName("Sampler")]
    public SamplerCardViewModel SamplerCardViewModel { get; }

    [JsonPropertyName("BatchSize")]
    public BatchSizeCardViewModel BatchSizeCardViewModel { get; }

    [JsonPropertyName("Seed")]
    public SeedCardViewModel SeedCardViewModel { get; }

    [JsonPropertyName("Prompt")]
    public PromptCardViewModel PromptCardViewModel { get; }

    [JsonPropertyName("VideoOutput")]
    public VideoOutputSettingsCardViewModel VideoOutputSettingsCardViewModel { get; }

    public InferenceMiniMaxH3TextToVideoViewModel(
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

        ModelCardViewModel = vmFactory.Get<MiniMaxH3ModelCardViewModel>();

        SamplerCardViewModel = vmFactory.Get<SamplerCardViewModel>(samplerCard =>
        {
            samplerCard.IsDimensionsEnabled = true;
            samplerCard.IsCfgScaleEnabled = false;
            samplerCard.IsSamplerSelectionEnabled = true;
            samplerCard.IsSchedulerSelectionEnabled = true;
            samplerCard.IsDenoiseStrengthEnabled = false;
            samplerCard.EnableAddons = false;
            samplerCard.IsLengthEnabled = true;
            samplerCard.SelectedSampler = ComfySampler.Euler;
            samplerCard.SelectedScheduler = ComfyScheduler.Simple;
            samplerCard.Steps = 20;
            samplerCard.CfgScale = 1.0d;
            samplerCard.Width = 1344;
            samplerCard.Height = 768;
            samplerCard.Length = 73;
        });

        PromptCardViewModel = AddDisposable(vmFactory.Get<PromptCardViewModel>());
        BatchSizeCardViewModel = vmFactory.Get<BatchSizeCardViewModel>();
        VideoOutputSettingsCardViewModel = vmFactory.Get<VideoOutputSettingsCardViewModel>(vm =>
        {
            vm.Fps = 24;
            vm.AddAudio = true;
            vm.SupportsLtxNativeAudio = true;
            vm.SelectedAudioSource = VideoAudioSource.Ltx;
        });

        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
        StackCardViewModel.AddCards(
            ModelCardViewModel,
            SamplerCardViewModel,
            SeedCardViewModel,
            BatchSizeCardViewModel,
            VideoOutputSettingsCardViewModel
        );
    }

    protected virtual bool ImageOnly => false;

    protected virtual ImageNodeConnection? LoadFirstFrame(ModuleApplyStepEventArgs e) => null;

    protected virtual (
        ImageNodeConnection? Frames,
        AudioNodeConnection? Audio
    ) LoadRefVideo(ModuleApplyStepEventArgs e) => (null, null);

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
        BatchSizeCardViewModel.ApplyStep(applyArgs);
        ModelCardViewModel.ApplyStep(applyArgs);

        var prompt = PromptCardViewModel.GetPrompt();
        var promptText = prompt.ProcessedText ?? prompt.RawText ?? "";

        var firstFrame = LoadFirstFrame(applyArgs);
        var (refFrames, refAudio) = LoadRefVideo(applyArgs);
        if (ModelCardViewModel.UseRef2Va && refFrames is not null && !promptText.Contains("<video_1>"))
            promptText = promptText.Trim() + "\nUse <video_1> as the motion and identity reference.";

        var steps = SamplerCardViewModel.Steps;
        if (ModelCardViewModel.EnableTurbo)
            steps = Math.Min(steps, 8);

        var result = MiniMaxH3ComfyPipeline.Sample(
            new MiniMaxH3ComfyPipeline.SampleArgs
            {
                EventArgs = applyArgs,
                Model = builder.Connections.Base.Model!,
                Clip = builder.Connections.Base.Clip!,
                VideoVae = builder.Connections.GetDefaultVAE(),
                AudioVae = builder.Connections.LtxAudioVae!,
                Prompt = promptText,
                Sampler = SamplerCardViewModel.SelectedSampler ?? ComfySampler.Euler,
                Scheduler = SamplerCardViewModel.SelectedScheduler ?? ComfyScheduler.Simple,
                Steps = steps,
                Seed = builder.Connections.Seed,
                Width = SamplerCardViewModel.Width,
                Height = SamplerCardViewModel.Height,
                Length = SamplerCardViewModel.Length,
                UseRef2Va = ModelCardViewModel.UseRef2Va,
                FirstFrame = firstFrame,
                RefVideoFrames = refFrames,
                RefVideoAudio = refAudio,
                ImageOnly = ImageOnly,
            }
        );

        builder.Connections.Primary = result.Images;
        builder.Connections.VideoFrameCount = MiniMaxH3ComfyPipeline.SnapLength(SamplerCardViewModel.Length);
        builder.Connections.LtxPassthroughAudio = result.Audio;

        applyArgs.InvokeAllPreOutputActions();

        if (ImageOnly)
        {
            var save = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.SaveImage
                {
                    Name = builder.Nodes.GetUniqueName("SaveImage"),
                    Images = result.Images,
                    FilenamePrefix = "MiniMaxH3",
                }
            );
            builder.Connections.OutputNodes.Add(save);
        }
        else
        {
            VideoOutputSettingsCardViewModel.ApplyStep(applyArgs);
        }
    }

    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        if (!await CheckClientConnectedWithPrompt() || !ClientManager.IsConnected)
            return;

        if (!await ModelCardViewModel.ValidateModel())
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
        PromptCardViewModel.LoadStateFromParameters(parameters);
        VideoOutputSettingsCardViewModel.LoadStateFromParameters(parameters);
        SeedCardViewModel.Seed = Convert.ToInt64(parameters.Seed);
    }

    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        parameters = SamplerCardViewModel.SaveStateToParameters(parameters);
        parameters = PromptCardViewModel.SaveStateToParameters(parameters);
        parameters = VideoOutputSettingsCardViewModel.SaveStateToParameters(parameters);
        parameters.Seed = (ulong)SeedCardViewModel.Seed;
        return parameters;
    }
}
