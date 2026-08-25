using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(AnimaIpAdapterCard))]
[ManagedService]
[RegisterTransient<AnimaIpAdapterCardViewModel>]
public partial class AnimaIpAdapterCardViewModel : LoadableViewModelBase
{
    public AnimaIpAdapterCardViewModel(IModelIndexService modelIndexService)
    {
        IpAdapterModels = new ObservableCollection<HybridModelFile>(
            modelIndexService.FindByModelType(SharedFolderType.IpAdapter).Select(HybridModelFile.FromLocal)
        );

        SelectedIpAdapter =
            IpAdapterModels.FirstOrDefault(m =>
                m.FileName.Contains("ip_adapter", StringComparison.OrdinalIgnoreCase)
            ) ?? IpAdapterModels.FirstOrDefault();
    }

    public ObservableCollection<HybridModelFile> IpAdapterModels { get; }

    [ObservableProperty]
    private HybridModelFile? selectedIpAdapter;

    [ObservableProperty]
    private bool isEnabled = true;

    [ObservableProperty]
    private double strength = 1.0;

    [ObservableProperty]
    private double ipCfgScale = 4.0;

    [ObservableProperty]
    private bool autoDownloadSiglip = true;

    [ObservableProperty]
    private bool useLora = true;

    public ModelNodeConnection Apply(
        ModuleApplyStepEventArgs e,
        ModelNodeConnection model,
        ImageNodeConnection refImage
    )
    {
        if (!IsEnabled)
            return model;

        var adapterName = Path.GetFileName(SelectedIpAdapter?.RelativePath);
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            throw new ValidationException(
                "Anima IP-Adapter weights not found. Put ip_adapter.safetensors in Models/IpAdapter "
                    + "(from LuciferTC/Anima-IP-Adapter)."
            );
        }

        var loader = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.AnimaIPAdapterLoader
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.AnimaIPAdapterLoader)),
                IpAdapterName = adapterName,
                AutoDownload = AutoDownloadSiglip,
            }
        );

        return e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.AnimaIPAdapterApply
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.AnimaIPAdapterApply)),
                    Model = model,
                    IpAdapter = loader.Output,
                    RefImage = refImage,
                    Strength = Strength,
                    IpCfgScale = IpCfgScale,
                    UseLora = UseLora,
                }
            )
            .Output;
    }
}
