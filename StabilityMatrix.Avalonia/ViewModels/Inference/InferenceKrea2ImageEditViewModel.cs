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
using StabilityMatrix.Core.Models.Notifications;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;
using Notification = DesktopNotifications.Notification;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceKrea2ImageEditView), IsPersistent = true)]
[RegisterScoped<InferenceKrea2ImageEditViewModel>, ManagedService]
public class InferenceKrea2ImageEditViewModel : InferenceGenerationViewModelBase, IParametersLoadableState
{
    private const int DefaultGroundingPx = 1024;

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

    /// <summary>Reference 1 (required).</summary>
    [JsonPropertyName("ImageLoader")]
    public SelectImageCardViewModel SelectImageCardViewModel { get; }

    [JsonPropertyName("ImageLoader2")]
    public SelectImageCardViewModel SelectImageCardViewModel2 { get; }

    [JsonPropertyName("ImageLoader3")]
    public SelectImageCardViewModel SelectImageCardViewModel3 { get; }

    [JsonPropertyName("ImageLoader4")]
    public SelectImageCardViewModel SelectImageCardViewModel4 { get; }

    [JsonPropertyName("ImageLoader5")]
    public SelectImageCardViewModel SelectImageCardViewModel5 { get; }

    [JsonIgnore]
    public IReadOnlyList<SelectImageCardViewModel> ReferenceImageCards { get; }

    public InferenceKrea2ImageEditViewModel(
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
            samplerCard.IsSchedulerSelectionEnabled = true;
            samplerCard.IsDenoiseStrengthEnabled = true;
            samplerCard.DenoiseStrength = 1.0d;
            samplerCard.EnableAddons = true;
            samplerCard.IsLengthEnabled = false;
            samplerCard.SelectedSampler = ComfySampler.Euler;
            samplerCard.SelectedScheduler = ComfyScheduler.Simple;
            samplerCard.Steps = 8;
            samplerCard.CfgScale = 1.0d;
            samplerCard.Width = 1024;
            samplerCard.Height = 1024;
        });

        PromptCardViewModel = AddDisposable(vmFactory.Get<PromptCardViewModel>());
        BatchSizeCardViewModel = vmFactory.Get<BatchSizeCardViewModel>();

        SelectImageCardViewModel = CreateImageCard(vmFactory, syncSize: true);
        SelectImageCardViewModel2 = CreateImageCard(vmFactory);
        SelectImageCardViewModel3 = CreateImageCard(vmFactory);
        SelectImageCardViewModel4 = CreateImageCard(vmFactory);
        SelectImageCardViewModel5 = CreateImageCard(vmFactory);

        ReferenceImageCards =
        [
            SelectImageCardViewModel,
            SelectImageCardViewModel2,
            SelectImageCardViewModel3,
            SelectImageCardViewModel4,
            SelectImageCardViewModel5,
        ];

        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
        StackCardViewModel.AddCards(
            ModelCardViewModel,
            SamplerCardViewModel,
            SeedCardViewModel,
            BatchSizeCardViewModel
        );
    }

    private static SelectImageCardViewModel CreateImageCard(
        IServiceManager<ViewModelBase> vmFactory,
        bool syncSize = false
    ) =>
        vmFactory.Get<SelectImageCardViewModel>(vm =>
        {
            vm.SyncBitmapSizeToTabContext = syncSize;
        });

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
                    Type = "krea2",
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

        // Identity-edit LoRAs are UNet-only; applying CLIP strength fries Qwen3-VL conditioning.
        foreach (var loraModule in ModelCardViewModel.ExtraNetworksStackCardViewModel.Cards.OfType<LoraModule>())
        {
            if (!loraModule.IsEnabled)
                continue;

            var card = loraModule.GetCard<ExtraNetworkCardViewModel>();
            if (card.SelectedModel?.RelativePath is not { } loraPath)
                continue;

            var loraLoader = nodes.AddNamedNode(
                ComfyNodeBuilder.LoraLoaderModelOnly(
                    nodes.GetUniqueName("LoraLoaderModelOnly"),
                    model,
                    loraPath,
                    card.ModelWeight
                )
            );
            model = loraLoader.Output;
            builder.Connections.Base.Model = model;
        }

        var positivePrompt = PromptCardViewModel.GetPrompt();
        positivePrompt.Process();
        var negativePrompt = PromptCardViewModel.GetNegativePrompt();
        negativePrompt.Process();

        var refImages = GetSelectedReferenceImages().ToList();
        if (refImages.Count == 0)
            throw new ValidationException("No image selected");

        // Match output canvas to primary reference — AR mismatch + fit mode caused rainbow noise.
        var primarySize = SelectImageCardViewModel.CurrentBitmapSize;
        var width = AlignDimension(
            primarySize.Width > 0 ? primarySize.Width : SamplerCardViewModel.Width
        );
        var height = AlignDimension(
            primarySize.Height > 0 ? primarySize.Height : SamplerCardViewModel.Height
        );

        var loadedImages = new ImageNodeConnection[refImages.Count];
        var latents = new LatentNodeConnection[refImages.Count];

        for (var i = 0; i < refImages.Count; i++)
        {
            var loadImage = nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadImage
                {
                    Name = nodes.GetUniqueName($"LoadImage_Ref{i + 1}"),
                    Image = refImages[i].GetHashGuidFileNameCached("Inference"),
                }
            );
            loadedImages[i] = loadImage.Output1;

            latents[i] = nodes
                .AddTypedNode(
                    new ComfyNodeBuilder.VAEEncode
                    {
                        Name = nodes.GetUniqueName($"VAEEncode_Ref{i + 1}"),
                        Pixels = loadImage.Output1,
                        Vae = vae,
                    }
                )
                .Output;
        }

        var positiveEncode = nodes.AddTypedNode(
            BuildGroundedEncode(
                nodes.GetUniqueName("Krea2EditGroundedEncode_Positive"),
                clip,
                positivePrompt.ProcessedText ?? string.Empty,
                loadedImages
            )
        );

        var negativeEncode = nodes.AddTypedNode(
            BuildGroundedEncode(
                nodes.GetUniqueName("Krea2EditGroundedEncode_Negative"),
                clip,
                negativePrompt.ProcessedText ?? string.Empty,
                loadedImages
            )
        );

        var emptyLatent = nodes.AddTypedNode(
            new ComfyNodeBuilder.EmptySD3LatentImage
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.EmptySD3LatentImage)),
                Width = width,
                Height = height,
                BatchSize = builder.Connections.BatchSize,
            }
        );

        model = nodes
            .AddTypedNode(
                BuildModelPatch(
                    nodes.GetUniqueName("Krea2EditModelPatch"),
                    model,
                    vae,
                    latents,
                    loadedImages
                )
            )
            .Output;

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
                Positive = positiveEncode.Output,
                Negative = negativeEncode.Output,
                LatentImage = emptyLatent.Output,
                Denoise = SamplerCardViewModel.DenoiseStrength,
            }
        );

        builder.Connections.Primary = sampler.Output;

        applyArgs.InvokeAllPreOutputActions();
        builder.SetupOutputImage();
    }

    private static ComfyNodeBuilder.Krea2EditGroundedEncode BuildGroundedEncode(
        string name,
        ClipNodeConnection clip,
        string prompt,
        IReadOnlyList<ImageNodeConnection> images
    ) =>
        new()
        {
            Name = name,
            Clip = clip,
            Prompt = prompt,
            GroundingPx = DefaultGroundingPx,
            Image = images.ElementAtOrDefault(0),
            ImageB = images.ElementAtOrDefault(1),
            ImageC = images.ElementAtOrDefault(2),
            ImageD = images.ElementAtOrDefault(3),
            ImageE = images.ElementAtOrDefault(4),
        };

    private static ComfyNodeBuilder.Krea2EditModelPatch BuildModelPatch(
        string name,
        ModelNodeConnection model,
        VAENodeConnection vae,
        IReadOnlyList<LatentNodeConnection> latents,
        IReadOnlyList<ImageNodeConnection> images
    ) =>
        new()
        {
            Name = name,
            Model = model,
            Vae = vae,
            // Matched source size uses crop; fit with large AR mismatch rainbow-noise'd on the 5-ref fork.
            FitMode = "crop (legacy)",
            RefBoost = 1.0,
            RefBoostB = 1.0,
            RefBoostC = 1.0,
            RefBoostD = 1.0,
            RefBoostE = 1.0,
            SourceLatent = latents.ElementAtOrDefault(0),
            SourceLatentB = latents.ElementAtOrDefault(1),
            SourceLatentC = latents.ElementAtOrDefault(2),
            SourceLatentD = latents.ElementAtOrDefault(3),
            SourceLatentE = latents.ElementAtOrDefault(4),
            SourceImage = images.ElementAtOrDefault(0),
            SourceImageB = images.ElementAtOrDefault(1),
            SourceImageC = images.ElementAtOrDefault(2),
            SourceImageD = images.ElementAtOrDefault(3),
            SourceImageE = images.ElementAtOrDefault(4),
        };

    private IEnumerable<ImageSource> GetSelectedReferenceImages()
    {
        foreach (var card in ReferenceImageCards)
        {
            if (card.ImageSource is { } image)
                yield return image;
        }
    }

    /// <summary>Wan/Krea2 latent grid requires multiples of 16.</summary>
    private static int AlignDimension(int value) => Math.Max(16, value / 16 * 16);

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
            notificationService.Show(
                "No Image",
                "Please select at least one reference image.",
                NotificationType.Warning
            );
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
    protected override IEnumerable<ImageSource> GetInputImages() => GetSelectedReferenceImages();

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
