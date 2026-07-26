using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceLtxvTextToVideoView>]
public partial class InferenceLtxvTextToVideoView : DockUserControlBase
{
    public InferenceLtxvTextToVideoView()
    {
        InitializeComponent();
    }
}
