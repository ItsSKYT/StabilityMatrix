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

[View(typeof(InferenceQwenImageEditView), IsPersistent = true)]
[RegisterScoped<InferenceQwenImageEditViewModel>, ManagedService]
public class InferenceQwenImageEditViewModel : InferenceGenerationViewModelBase, IParametersLoadableState
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

    public InferenceQwenImageEditViewModel(
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
        ModelCardViewModel.Shift = 3.1d;

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
            samplerCard.Steps = 4;
            samplerCard.CfgScale = 1.0d;
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

        model = nodes
            .AddTypedNode(
                new ComfyNodeBuilder.ModelSamplingAuraFlow
                {
                    Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.ModelSamplingAuraFlow)),
                    Model = model,
                    Shift = ModelCardViewModel.Shift,
                }
            )
            .Output;

        var clip = nodes
            .AddTypedNode(
                new ComfyNodeBuilder.CLIPLoader
                {
                    Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.CLIPLoader)),
                    ClipName = clipPath,
                    Type = "qwen_image",
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

        var positiveEncode = nodes.AddTypedNode(
            new ComfyNodeBuilder.TextEncodeQwenImageEditPlus
            {
                Name = nodes.GetUniqueName("TextEncodeQwenImageEditPlus_Positive"),
                Clip = clip,
                Vae = vae,
                Image1 = loadImage.Output1,
                Prompt = positivePrompt.ProcessedText ?? string.Empty,
            }
        );

        var negativeEncode = nodes.AddTypedNode(
            new ComfyNodeBuilder.TextEncodeQwenImageEditPlus
            {
                Name = nodes.GetUniqueName("TextEncodeQwenImageEditPlus_Negative"),
                Clip = clip,
                Vae = vae,
                Image1 = loadImage.Output1,
                Prompt = negativePrompt.ProcessedText ?? string.Empty,
            }
        );

        var emptyLatent = nodes.AddTypedNode(
            new ComfyNodeBuilder.EmptySD3LatentImage
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.EmptySD3LatentImage)),
                Width = SamplerCardViewModel.Width,
                Height = SamplerCardViewModel.Height,
                BatchSize = builder.Connections.BatchSize,
            }
        );

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
