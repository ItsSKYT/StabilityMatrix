using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceLtxvLipDubView>]
public partial class InferenceLtxvLipDubView : DockUserControlBase
{
    public InferenceLtxvLipDubView()
    {
        InitializeComponent();
    }
}
