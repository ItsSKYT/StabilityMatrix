using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceLtxvImageToVideoView>]
public partial class InferenceLtxvImageToVideoView : DockUserControlBase
{
    public InferenceLtxvImageToVideoView()
    {
        InitializeComponent();
    }
}
