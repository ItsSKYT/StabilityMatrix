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
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
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

[View(typeof(InferenceFlux2KleinImageEditView), IsPersistent = true)]
[RegisterScoped<InferenceFlux2KleinImageEditViewModel>, ManagedService]
public class InferenceFlux2KleinImageEditViewModel
    : InferenceGenerationViewModelBase,
        IParametersLoadableState
{
    private readonly INotificationService notificationService;

    [JsonIgnore]
    public StackCardViewModel StackCardViewModel { get; }

    [JsonPropertyName("Model")]
    public WanModelCardViewModel ModelCardViewModel { get; }

    [JsonPropertyName("Sampler")]
    public SamplerCardViewModel SamplerCardViewModel { get; }

    [JsonPropertyName("BatchSize")]
    public BatchSizeCardViewModel BatchSizeCardViewModel { get; }

    [JsonPropertyName("Seed")]
    public SeedCardViewModel SeedCardViewModel { get; }

    [JsonPropertyName("Prompt")]
    public PromptCardViewModel PromptCardViewModel { get; }

    [JsonPropertyName("ImageLoader")]
    public SelectImageCardViewModel SelectImageCardViewModel { get; }

    public InferenceFlux2KleinImageEditViewModel(
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

        ModelCardViewModel = vmFactory.Get<WanModelCardViewModel>();
        ModelCardViewModel.IsClipVisionEnabled = false;

        SamplerCardViewModel = vmFactory.Get<SamplerCardViewModel>(samplerCard =>
        {
            samplerCard.IsDimensionsEnabled = true;
            samplerCard.IsCfgScaleEnabled = true;
            samplerCard.IsSamplerSelectionEnabled = true;
            samplerCard.IsSchedulerSelectionEnabled = false;
            samplerCard.IsDenoiseStrengthEnabled = false;
            samplerCard.EnableAddons = true;
            samplerCard.IsLengthEnabled = false;
            samplerCard.SelectedSampler = ComfySampler.Euler;
            samplerCard.Steps = 12;
            samplerCard.CfgScale = 1.1d;
            samplerCard.Width = 1024;
            samplerCard.Height = 1024;
        });

        PromptCardViewModel = AddDisposable(vmFactory.Get<PromptCardViewModel>());
        BatchSizeCardViewModel = vmFactory.Get<BatchSizeCardViewModel>();
        SelectImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>(vm =>
        {
            vm.SyncBitmapSizeToTabContext = true;
        });

        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
        StackCardViewModel.AddCards(
            ModelCardViewModel,
            SamplerCardViewModel,
            SeedCardViewModel,
            BatchSizeCardViewModel
        );
    }

    /// <inheritdoc />
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

        var unetPath =
            ModelCardViewModel.SelectedModel?.RelativePath
            ?? throw new ValidationException("Model not selected");
        var clipPath =
            ModelCardViewModel.SelectedClipModel?.RelativePath
            ?? throw new ValidationException("No Clip Model Selected");
        var vaePath =
            ModelCardViewModel.SelectedVae?.RelativePath ?? throw new ValidationException("No VAE Selected");

        ModelNodeConnection model;
        if (unetPath.EndsWith("gguf", StringComparison.OrdinalIgnoreCase))
        {
            model = nodes
                .AddTypedNode(
                    new ComfyNodeBuilder.UnetLoaderGGUF
                    {
                        Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.UnetLoaderGGUF)),
                        UnetName = unetPath,
                    }
                )
                .Output;
        }
        else
        {
            model = nodes
                .AddTypedNode(
                    new ComfyNodeBuilder.UNETLoader
                    {
                        Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.UNETLoader)),
                        UnetName = unetPath,
                        WeightDtype = ModelCardViewModel.SelectedDType ?? "default",
                    }
                )
                .Output;
        }

        var clip = nodes
            .AddTypedNode(
                new ComfyNodeBuilder.CLIPLoader
                {
                    Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.CLIPLoader)),
                    ClipName = clipPath,
                    Type = "flux2",
                }
            )
            .Output;

        var vae = nodes
            .AddTypedNode(
                new ComfyNodeBuilder.VAELoader
                {
                    Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.VAELoader)),
                    VaeName = vaePath,
                }
            )
            .Output;

        builder.Connections.Base.Model = model;
        builder.Connections.Base.Clip = clip;
        builder.Connections.Base.VAE = vae;
        builder.Connections.PrimaryVAE = vae;

        if (ModelCardViewModel.ExtraNetworksStackCardViewModel.Cards.OfType<LoraModule>().Any(x => x.IsEnabled))
        {
            ModelCardViewModel.ExtraNetworksStackCardViewModel.ApplyStep(applyArgs);
            model = builder.Connections.Base.Model!;
            clip = builder.Connections.Base.Clip!;
        }

        var positivePrompt = PromptCardViewModel.GetPrompt();
        positivePrompt.Process();
        var negativePrompt = PromptCardViewModel.GetNegativePrompt();
        negativePrompt.Process();

        var positiveEncode = nodes.AddTypedNode(
            new ComfyNodeBuilder.CLIPTextEncode
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.CLIPTextEncode)),
                Clip = clip,
                Text = positivePrompt.ProcessedText ?? string.Empty,
            }
        );

        var negativeEncode = nodes.AddTypedNode(
            new ComfyNodeBuilder.CLIPTextEncode
            {
                Name = nodes.GetUniqueName("CLIPTextEncode_Negative"),
                Clip = clip,
                Text = negativePrompt.ProcessedText ?? string.Empty,
            }
        );

        var imageSource =
            SelectImageCardViewModel.ImageSource
            ?? throw new ValidationException("No image selected");
        var loadImage = nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadImage
            {
                Name = nodes.GetUniqueName("LoadImage"),
                Image = imageSource.GetHashGuidFileNameCached("Inference"),
            }
        );

        var scaledImage = nodes.AddTypedNode(
            new ComfyNodeBuilder.ImageScaleToTotalPixels
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.ImageScaleToTotalPixels)),
                Image = loadImage.Output1,
                UpscaleMethod = "nearest-exact",
                Megapixels = 1.0,
                ResolutionSteps = 16,
            }
        );

        var referenceLatent = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEEncode
            {
                Name = nodes.GetUniqueName("VAEEncode_Ref"),
                Pixels = scaledImage.Output,
                Vae = vae,
            }
        );

        var positiveConditioning = nodes
            .AddTypedNode(
                new ComfyNodeBuilder.ReferenceLatent
                {
                    Name = nodes.GetUniqueName("ReferenceLatent_Positive"),
                    Conditioning = positiveEncode.Output,
                    Latent = referenceLatent.Output,
                }
            )
            .Output;

        var negativeConditioning = nodes
            .AddTypedNode(
                new ComfyNodeBuilder.ReferenceLatent
                {
                    Name = nodes.GetUniqueName("ReferenceLatent_Negative"),
                    Conditioning = negativeEncode.Output,
                    Latent = referenceLatent.Output,
                }
            )
            .Output;

        var width = SamplerCardViewModel.Width;
        var height = SamplerCardViewModel.Height;

        var emptyLatent = nodes.AddTypedNode(
            new ComfyNodeBuilder.EmptyFlux2LatentImage
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.EmptyFlux2LatentImage)),
                Width = width,
                Height = height,
                BatchSize = builder.Connections.BatchSize,
            }
        );

        var cfgGuider = nodes.AddTypedNode(
            new ComfyNodeBuilder.CFGGuider
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.CFGGuider)),
                Model = model,
                Positive = positiveConditioning,
                Negative = negativeConditioning,
                Cfg = SamplerCardViewModel.CfgScale,
            }
        );

        var scheduler = nodes.AddTypedNode(
            new ComfyNodeBuilder.Flux2Scheduler
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.Flux2Scheduler)),
                Steps = SamplerCardViewModel.Steps,
                Width = width,
                Height = height,
            }
        );

        var samplerSelect = nodes.AddTypedNode(
            new ComfyNodeBuilder.KSamplerSelect
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.KSamplerSelect)),
                SamplerName =
                    SamplerCardViewModel.SelectedSampler?.Name
                    ?? throw new ValidationException("Sampler not selected"),
            }
        );

        var randomNoise = nodes.AddTypedNode(
            new ComfyNodeBuilder.RandomNoise
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.RandomNoise)),
                NoiseSeed = builder.Connections.Seed,
            }
        );

        var sampler = nodes.AddTypedNode(
            new ComfyNodeBuilder.SamplerCustomAdvanced
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.SamplerCustomAdvanced)),
                Noise = randomNoise.Output,
                Guider = cfgGuider.Output,
                Sampler = samplerSelect.Output,
                Sigmas = scheduler.Output,
                LatentImage = emptyLatent.Output,
            }
        );

        builder.Connections.Primary = sampler.Output1;

        applyArgs.InvokeAllPreOutputActions();
        builder.SetupOutputImage();
    }

    /// <inheritdoc />
    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        if (!await CheckClientConnectedWithPrompt() || !ClientManager.IsConnected)
        {
            return;
        }

        if (!await ModelCardViewModel.ValidateModel())
            return;

        if (SelectImageCardViewModel.ImageSource is null)
        {
            notificationService.Show("No Image", "Please select an image to edit.", NotificationType.Warning);
            return;
        }

        if (!await PromptCardViewModel.ValidatePrompts())
            return;

        var seedCard = SeedCardViewModel;
        if (overrides is not { UseCurrentSeed: true } && seedCard.IsRandomizeEnabled)
        {
            seedCard.GenerateNewSeed();
        }

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

    /// <inheritdoc />
    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (SelectImageCardViewModel.ImageSource is { } image)
        {
            yield return image;
        }
    }

    /// <inheritdoc />
    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        SamplerCardViewModel.LoadStateFromParameters(parameters);
        ModelCardViewModel.LoadStateFromParameters(parameters);
        PromptCardViewModel.LoadStateFromParameters(parameters);
        SeedCardViewModel.Seed = Convert.ToInt64(parameters.Seed);
    }

    /// <inheritdoc />
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        parameters = SamplerCardViewModel.SaveStateToParameters(parameters);
        parameters = ModelCardViewModel.SaveStateToParameters(parameters);
        parameters = PromptCardViewModel.SaveStateToParameters(parameters);
        parameters.Seed = (ulong)SeedCardViewModel.Seed;
        return parameters;
    }
}
