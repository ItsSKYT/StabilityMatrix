using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceMiniMaxH3VideoToVideoView>]
public partial class InferenceMiniMaxH3VideoToVideoView : DockUserControlBase
{
    public InferenceMiniMaxH3VideoToVideoView()
    {
        InitializeComponent();
    }
}
