using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceLtxvHdrIcLoraView>]
public partial class InferenceLtxvHdrIcLoraView : DockUserControlBase
{
    public InferenceLtxvHdrIcLoraView()
    {
        InitializeComponent();
    }
}
