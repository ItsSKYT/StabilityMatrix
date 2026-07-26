using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using StabilityMatrix.Avalonia.Models;

namespace StabilityMatrix.Avalonia.Controls;

/// <summary>
/// Looping video preview. Uses WebView2 HTML5 video for synced A/V (muted by default).
/// Mute chip lives inside the HTML — NativeControlHost would cover any Avalonia overlay.
/// </summary>
public partial class VideoPreviewPanel : UserControl
{
    public static readonly StyledProperty<ImageSource?> SourceProperty = AvaloniaProperty.Register<
        VideoPreviewPanel,
        ImageSource?
    >(nameof(Source));

    private bool isMuted = true;
    private string? boundVideoPath;

    public ImageSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public VideoPreviewPanel()
    {
        InitializeComponent();
        WebHost.Ready += (_, _) =>
        {
            if (boundVideoPath is not null)
                _ = StartWebVideoAsync(boundVideoPath);
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            isMuted = true;
            boundVideoPath = change.GetNewValue<ImageSource?>()?.LocalFile?.FullPath;
            if (WebHost is not null)
                RestartPlayback();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RestartPlayback();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopWebVideo();
        isMuted = true;
        base.OnDetachedFromVisualTree(e);
    }

    private void RestartPlayback()
    {
        StopWebVideo();

        var source = Source;
        boundVideoPath = source?.LocalFile?.FullPath;

        StillBox.Image = source?.Bitmap;
        if (source?.HasAnimatedPlayback == true && source.PlaybackPath is { } webp)
        {
            WebpPlayer.SourceUriRaw = webp;
            WebpPlayer.IsVisible = true;
        }
        else
        {
            WebpPlayer.IsVisible = false;
            WebpPlayer.SourceUriRaw = null!;
        }

        FallbackPanel.IsVisible = true;
        WebHost.IsVisible = false;

        if (
            boundVideoPath is not null
            && File.Exists(boundVideoPath)
            && OperatingSystem.IsWindows()
        )
        {
            _ = StartWebVideoAsync(boundVideoPath);
        }
    }

    private async Task StartWebVideoAsync(string videoPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(videoPath);
            var fileName = Path.GetFileName(videoPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName))
                return;

            await WebHost.MapFolderAsync(dir);
            var html = BuildVideoHtml(fileName, muted: isMuted);
            await WebHost.NavigateHtmlAsync(html);

            await Task.Delay(200);
            Dispatcher.UIThread.Post(() =>
            {
                if (boundVideoPath != videoPath)
                    return;
                WebHost.IsVisible = true;
                FallbackPanel.IsVisible = false;
            });
        }
        catch
        {
            Dispatcher.UIThread.Post(() =>
            {
                WebHost.IsVisible = false;
                FallbackPanel.IsVisible = true;
            });
        }
    }

    private void StopWebVideo()
    {
        try
        {
            _ = WebHost.NavigateHtmlAsync("<html><body style='background:#000'></body></html>");
        }
        catch
        {
            // ignore
        }

        WebHost.IsVisible = false;
        FallbackPanel.IsVisible = true;
    }

    private static string BuildVideoHtml(string fileName, bool muted)
    {
        var src = WebUtility.HtmlEncode("https://sm.video/" + fileName.Replace("\\", "/"));
        var mutedAttr = muted ? "muted" : "";
        var mutedJs = muted ? "true" : "false";

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.Append("<style>");
        sb.Append("html,body{margin:0;width:100%;height:100%;background:#000;overflow:hidden;}");
        sb.Append("video{width:100%;height:100%;object-fit:contain;display:block;}");
        sb.Append("#mute{position:fixed;right:8px;bottom:8px;width:28px;height:28px;padding:0;");
        sb.Append("border:none;border-radius:14px;background:rgba(0,0,0,.6);color:#fff;");
        sb.Append("cursor:pointer;z-index:10;display:flex;align-items:center;justify-content:center;}");
        sb.Append("#mute:hover{background:rgba(0,0,0,.8);}#mute svg{width:14px;height:14px;fill:#fff;}");
        sb.Append("</style></head><body>");
        sb.Append($"<video id='v' src='{src}' autoplay loop playsinline {mutedAttr}></video>");
        sb.Append("<button id='mute' type='button' title='Unmute' aria-label='Mute toggle'></button>");
        sb.Append("<script>");
        sb.Append("var muted=").Append(mutedJs).Append(';');
        sb.Append("var iconOff='<svg viewBox=\"0 0 24 24\"><path d=\"M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z\"/></svg>';");
        sb.Append("var iconOn='<svg viewBox=\"0 0 24 24\"><path d=\"M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z\"/></svg>';");
        sb.Append("var btn=document.getElementById('mute');var v=document.getElementById('v');");
        sb.Append("function paint(){btn.innerHTML=muted?iconOff:iconOn;btn.title=muted?'Unmute':'Mute';v.muted=!!muted;}");
        sb.Append("window.setMuted=function(m){muted=!!m;paint();if(!muted){v.play().catch(function(){});}};");
        sb.Append("btn.onclick=function(){window.setMuted(!muted);};");
        sb.Append("paint();");
        sb.Append("</script></body></html>");
        return sb.ToString();
    }
}
