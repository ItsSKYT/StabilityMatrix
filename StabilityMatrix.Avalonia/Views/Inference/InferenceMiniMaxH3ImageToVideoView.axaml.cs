using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceMiniMaxH3ImageToVideoView>]
public partial class InferenceMiniMaxH3ImageToVideoView : DockUserControlBase
{
    public InferenceMiniMaxH3ImageToVideoView()
    {
        InitializeComponent();
    }
}
