using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceLtxvAudioToVideoView>]
public partial class InferenceLtxvAudioToVideoView : DockUserControlBase
{
    public InferenceLtxvAudioToVideoView()
    {
        InitializeComponent();
    }
}
