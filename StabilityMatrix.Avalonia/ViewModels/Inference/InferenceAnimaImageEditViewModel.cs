using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Controls.Notifications;
using DesktopNotifications;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Models.Notifications;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;
using Notification = DesktopNotifications.Notification;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceAnimaImageEditView), IsPersistent = true)]
[RegisterScoped<InferenceAnimaImageEditViewModel>, ManagedService]
public class InferenceAnimaImageEditViewModel : InferenceGenerationViewModelBase, IParametersLoadableState
{
    private readonly INotificationService notificationService;

    [JsonIgnore]
    public StackCardViewModel StackCardViewModel { get; }

    [JsonPropertyName("Model")]
    public ModelCardViewModel ModelCardViewModel { get; }

    [JsonPropertyName("Sampler")]
    public SamplerCardViewModel SamplerCardViewModel { get; }

    [JsonPropertyName("BatchSize")]
    public BatchSizeCardViewModel BatchSizeCardViewModel { get; }

    [JsonPropertyName("Seed")]
    public SeedCardViewModel SeedCardViewModel { get; }

    [JsonPropertyName("Prompt")]
    public PromptCardViewModel PromptCardViewModel { get; }

    [JsonPropertyName("IpAdapter")]
    public AnimaIpAdapterCardViewModel IpAdapterCardViewModel { get; }

    [JsonPropertyName("ImageLoader")]
    public SelectImageCardViewModel SelectImageCardViewModel { get; }

    [JsonPropertyName("FaceImageLoader")]
    public SelectImageCardViewModel FaceImageCardViewModel { get; }

    public InferenceAnimaImageEditViewModel(
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

        ModelCardViewModel = vmFactory.Get<ModelCardViewModel>();
        ModelCardViewModel.RecommendedDefaultsRequested += ApplyRecommendedDefaults;

        SamplerCardViewModel = vmFactory.Get<SamplerCardViewModel>(samplerCard =>
        {
            samplerCard.IsDimensionsEnabled = true;
            samplerCard.IsCfgScaleEnabled = true;
            samplerCard.IsSamplerSelectionEnabled = true;
            samplerCard.IsSchedulerSelectionEnabled = true;
            samplerCard.IsDenoiseStrengthEnabled = true;
            samplerCard.DenoiseStrength = 0.7d;
            samplerCard.EnableAddons = true;
            samplerCard.IsLengthEnabled = false;
            samplerCard.SelectedSampler = ComfySampler.ErSde;
            samplerCard.SelectedScheduler = ComfyScheduler.Simple;
            samplerCard.Steps = 8;
            samplerCard.CfgScale = 4.0d;
            samplerCard.Width = 1024;
            samplerCard.Height = 1024;
        });

        PromptCardViewModel = AddDisposable(vmFactory.Get<PromptCardViewModel>());
        BatchSizeCardViewModel = vmFactory.Get<BatchSizeCardViewModel>();
        IpAdapterCardViewModel = vmFactory.Get<AnimaIpAdapterCardViewModel>();

        SelectImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>(vm =>
        {
            vm.SyncBitmapSizeToTabContext = true;
        });
        FaceImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>();

        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
        StackCardViewModel.AddCards(
            ModelCardViewModel,
            SamplerCardViewModel,
            ModelCardViewModel.ExtraNetworksStackCardViewModel,
            IpAdapterCardViewModel,
            SeedCardViewModel,
            BatchSizeCardViewModel
        );
    }

    private void ApplyRecommendedDefaults(InferenceWorkflowProfile profile)
    {
        if (profile is not InferenceWorkflowProfile.Anima)
            return;

        SamplerCardViewModel.SelectedSampler = ComfySampler.ErSde;
        SamplerCardViewModel.SelectedScheduler = ComfyScheduler.Simple;
        SamplerCardViewModel.Steps = 8;
        SamplerCardViewModel.CfgScale = 4.0d;
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);

        var applyArgs = args.ToModuleApplyStepEventArgs();
        var builder = args.Builder;
        var nodes = builder.Nodes;

        builder.Connections.Seed = args.SeedOverride switch
        {
            { } seed => Convert.ToUInt64(seed),
            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
        };

        BatchSizeCardViewModel.ApplyStep(applyArgs);
        ModelCardViewModel.ApplyStep(applyArgs);

        applyArgs.PreClipEncodeActions.Add(ModelCardViewModel.ApplyExtraNetworksStep);
        PromptCardViewModel.ApplyStep(applyArgs);

        var sceneImage = SelectImageCardViewModel.ImageSource;
        var faceImage = FaceImageCardViewModel.ImageSource ?? sceneImage;
        if (faceImage is null)
            throw new ValidationException("No image selected");

        var faceLoad = nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadImage
            {
                Name = nodes.GetUniqueName("LoadImage_Face"),
                Image = faceImage.GetHashGuidFileNameCached("Inference"),
            }
        );

        var model =
            builder.Connections.Base.Model ?? throw new ValidationException("Model not loaded");
        model = IpAdapterCardViewModel.Apply(applyArgs, model, faceLoad.Output1);
        builder.Connections.Base.Model = model;

        var vae =
            builder.Connections.PrimaryVAE
            ?? builder.Connections.Base.VAE
            ?? throw new ValidationException("VAE not loaded");

        LatentNodeConnection latent;
        if (SamplerCardViewModel.DenoiseStrength < 0.999 && sceneImage is not null)
        {
            var sceneLoad = nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadImage
                {
                    Name = nodes.GetUniqueName("LoadImage_Scene"),
                    Image = sceneImage.GetHashGuidFileNameCached("Inference"),
                }
            );

            latent = nodes
                .AddTypedNode(
                    new ComfyNodeBuilder.VAEEncode
                    {
                        Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.VAEEncode)),
                        Pixels = sceneLoad.Output1,
                        Vae = vae,
                    }
                )
                .Output;
        }
        else
        {
            latent = nodes
                .AddTypedNode(
                    new ComfyNodeBuilder.EmptyLatentImage
                    {
                        Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.EmptyLatentImage)),
                        Width = SamplerCardViewModel.Width,
                        Height = SamplerCardViewModel.Height,
                        BatchSize = builder.Connections.BatchSize,
                    }
                )
                .Output;
        }

        var conditioning =
            builder.Connections.Base.Conditioning
            ?? throw new ValidationException("Prompt conditioning missing");

        var sampler = nodes.AddTypedNode(
            new ComfyNodeBuilder.KSampler
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.KSampler)),
                Model = model,
                Seed = builder.Connections.Seed,
                Steps = SamplerCardViewModel.Steps,
                Cfg = SamplerCardViewModel.CfgScale,
                SamplerName =
                    SamplerCardViewModel.SelectedSampler?.Name
                    ?? throw new ValidationException("Sampler not selected"),
                Scheduler =
                    SamplerCardViewModel.SelectedScheduler?.Name
                    ?? throw new ValidationException("Scheduler not selected"),
                Positive = conditioning.Positive,
                Negative = conditioning.Negative,
                LatentImage = latent,
                Denoise = SamplerCardViewModel.DenoiseStrength,
            }
        );

        builder.Connections.Primary = sampler.Output;
        applyArgs.InvokeAllPreOutputActions();
        builder.SetupOutputImage();
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

        if (SelectImageCardViewModel.ImageSource is null && FaceImageCardViewModel.ImageSource is null)
        {
            notificationService.Show(
                "No Image",
                "Drop a character photo in Scene or Face.",
                NotificationType.Warning
            );
            return;
        }

        if (!await PromptCardViewModel.ValidatePrompts())
            return;

        var seedCard = SeedCardViewModel;
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
            {
                inferenceProject = inferenceProject.WithState(x => x["Seed"]["Seed"] = seed);
            }

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
        {
            await RunGeneration(args, cancellationToken);
        }

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

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (SelectImageCardViewModel.ImageSource is { } scene)
            yield return scene;
        if (FaceImageCardViewModel.ImageSource is { } face)
            yield return face;
    }

    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        SamplerCardViewModel.LoadStateFromParameters(parameters);
        ModelCardViewModel.LoadStateFromParameters(parameters);
        PromptCardViewModel.LoadStateFromParameters(parameters);
        SeedCardViewModel.Seed = Convert.ToInt64(parameters.Seed);
    }

    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        parameters = SamplerCardViewModel.SaveStateToParameters(parameters);
        parameters = ModelCardViewModel.SaveStateToParameters(parameters);
        parameters = PromptCardViewModel.SaveStateToParameters(parameters);
        parameters.Seed = (ulong)SeedCardViewModel.Seed;
        return parameters;
    }
}
