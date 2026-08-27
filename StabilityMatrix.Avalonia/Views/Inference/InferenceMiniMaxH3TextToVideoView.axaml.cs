using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceMiniMaxH3TextToVideoView>]
public partial class InferenceMiniMaxH3TextToVideoView : DockUserControlBase
{
    public InferenceMiniMaxH3TextToVideoView()
    {
        InitializeComponent();
    }
}
