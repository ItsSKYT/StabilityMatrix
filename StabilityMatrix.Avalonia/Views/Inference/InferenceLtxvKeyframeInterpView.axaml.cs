using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceLtxvKeyframeInterpView>]
public partial class InferenceLtxvKeyframeInterpView : DockUserControlBase
{
    public InferenceLtxvKeyframeInterpView()
    {
        InitializeComponent();
    }
}
