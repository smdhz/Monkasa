using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Monkasa.ViewModels;

namespace Monkasa.Views;

public partial class ImageViewer : UserControl
{
    private const double MinViewerZoom = 1.0;
    private const double MaxViewerZoom = 2.0;
    private const double ViewerZoomStep = 0.1;

    private MainWindowViewModel? _attachedViewModel;
    private bool _isViewerPanning;
    private Point _viewerPanStartPoint;
    private Vector _viewerPanStartOffset;
    private Vector _viewerPanOffset;
    private double _viewerZoom = MinViewerZoom;
    private readonly ScaleTransform _viewerScaleTransform = new(1, 1);

    public ImageViewer()
    {
        InitializeComponent();
        ViewerImageElement.RenderTransform = _viewerScaleTransform;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            AttachViewModel(viewModel);
        }
        else if (_attachedViewModel is not null)
        {
            _attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _attachedViewModel = null;
        }
    }

    private void OnViewerOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsFromImageElement(e.Source))
        {
            return;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.CloseViewerCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewerImagePressed(object? sender, PointerPressedEventArgs e)
    {
        // Do not mark handled here, so viewport can start drag/pan on image area.
    }

    private bool IsFromImageElement(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        foreach (var ancestor in visual.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(ancestor, ViewerImageElement))
            {
                return true;
            }
        }

        return false;
    }

    private void OnViewerViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyViewerTransform();
    }

    private void OnViewerViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_attachedViewModel is null || !_attachedViewModel.IsViewerOpen || ViewerImageElement.Source is null)
        {
            return;
        }

        var deltaY = e.Delta.Y;
        if (Math.Abs(deltaY) < double.Epsilon)
        {
            return;
        }

        var nextZoom = Math.Clamp(
            _viewerZoom + (deltaY > 0 ? ViewerZoomStep : -ViewerZoomStep),
            MinViewerZoom,
            MaxViewerZoom);

        if (Math.Abs(nextZoom - _viewerZoom) < double.Epsilon)
        {
            return;
        }

        _viewerZoom = nextZoom;
        ApplyViewerTransform();
        e.Handled = true;
    }

    private void OnViewerViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_attachedViewModel is null || !_attachedViewModel.IsViewerOpen)
        {
            return;
        }

        if (!e.GetCurrentPoint(ViewerViewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!IsFromImageElement(e.Source))
        {
            return;
        }

        if (!CanPanViewer())
        {
            return;
        }

        _isViewerPanning = true;
        _viewerPanStartPoint = e.GetPosition(ViewerViewport);
        _viewerPanStartOffset = _viewerPanOffset;
        e.Pointer.Capture(ViewerViewport);
        e.Handled = true;
    }

    private void OnViewerViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isViewerPanning)
        {
            return;
        }

        var currentPoint = e.GetPosition(ViewerViewport);
        var delta = currentPoint - _viewerPanStartPoint;
        _viewerPanOffset = _viewerPanStartOffset + delta;
        ApplyViewerTransform();
        e.Handled = true;
    }

    private void OnViewerViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isViewerPanning)
        {
            return;
        }

        _isViewerPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnViewerViewportPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isViewerPanning = false;
    }

    private void AttachViewModel(MainWindowViewModel viewModel)
    {
        if (ReferenceEquals(_attachedViewModel, viewModel))
        {
            return;
        }

        if (_attachedViewModel is not null)
        {
            _attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _attachedViewModel = viewModel;
        _attachedViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_attachedViewModel is null)
        {
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsViewerOpen))
        {
            if (_attachedViewModel.IsViewerOpen)
            {
                ResetViewerTransform();
                UpdateViewerImageSize();
            }
            else
            {
                _isViewerPanning = false;
                ResetViewerTransform();
            }

            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.ViewerImage))
        {
            _isViewerPanning = false;
            ResetViewerTransform();
            UpdateViewerImageSize();
        }
    }

    private void ResetViewerTransform()
    {
        _viewerZoom = MinViewerZoom;
        _viewerPanOffset = default;
        ApplyViewerTransform();
    }

    private void UpdateViewerImageSize()
    {
        if (ViewerImageElement.Source is not Bitmap bitmap)
        {
            ViewerImageElement.Width = double.NaN;
            ViewerImageElement.Height = double.NaN;
            return;
        }

        var dpiX = bitmap.Dpi.X <= 0 ? 96d : bitmap.Dpi.X;
        var dpiY = bitmap.Dpi.Y <= 0 ? 96d : bitmap.Dpi.Y;

        ViewerImageElement.Width = bitmap.PixelSize.Width * 96d / dpiX;
        ViewerImageElement.Height = bitmap.PixelSize.Height * 96d / dpiY;
    }

    private bool CanPanViewer()
    {
        var viewportSize = ViewerViewport.Bounds.Size;
        var scaledSize = GetScaledViewerImageSize(viewportSize);
        return scaledSize.Width > viewportSize.Width || scaledSize.Height > viewportSize.Height;
    }

    private Size GetScaledViewerImageSize(Size viewportSize)
    {
        var baseSize = GetViewerImageBaseSize();
        if (baseSize.Width <= 0 || baseSize.Height <= 0)
        {
            return default;
        }

        var totalScale = GetViewerTotalScale(baseSize, viewportSize);
        return new Size(baseSize.Width * totalScale, baseSize.Height * totalScale);
    }

    private double GetViewerTotalScale(Size baseSize, Size viewportSize)
    {
        if (baseSize.Width <= 0 || baseSize.Height <= 0 || viewportSize.Width <= 0 || viewportSize.Height <= 0)
        {
            return 1d;
        }

        // Default 100% = fit into viewport, but do not upscale original image by default.
        var fitScale = Math.Min(viewportSize.Width / baseSize.Width, viewportSize.Height / baseSize.Height);
        var baseScale = Math.Min(1d, fitScale);
        return baseScale * _viewerZoom;
    }

    private Size GetViewerImageBaseSize()
    {
        if (double.IsFinite(ViewerImageElement.Width) &&
            double.IsFinite(ViewerImageElement.Height) &&
            ViewerImageElement.Width > 0 &&
            ViewerImageElement.Height > 0)
        {
            return new Size(ViewerImageElement.Width, ViewerImageElement.Height);
        }

        return ViewerImageElement.Bounds.Size;
    }

    private void ApplyViewerTransform()
    {
        if (ViewerImageElement.Source is null)
        {
            return;
        }

        UpdateViewerImageSize();

        var viewportSize = ViewerViewport.Bounds.Size;
        var baseSize = GetViewerImageBaseSize();
        var scaledSize = GetScaledViewerImageSize(viewportSize);
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0 || scaledSize.Width <= 0 || scaledSize.Height <= 0)
        {
            return;
        }

        _viewerPanOffset = ClampViewerPanOffset(_viewerPanOffset, scaledSize, viewportSize);

        var totalScale = GetViewerTotalScale(baseSize, viewportSize);
        _viewerScaleTransform.ScaleX = totalScale;
        _viewerScaleTransform.ScaleY = totalScale;

        var centerX = (viewportSize.Width - scaledSize.Width) / 2d;
        var centerY = (viewportSize.Height - scaledSize.Height) / 2d;
        Canvas.SetLeft(ViewerImageElement, centerX + _viewerPanOffset.X);
        Canvas.SetTop(ViewerImageElement, centerY + _viewerPanOffset.Y);
    }

    private static Vector ClampViewerPanOffset(Vector offset, Size scaledSize, Size viewportSize)
    {
        var maxX = Math.Max(0d, (scaledSize.Width - viewportSize.Width) / 2d);
        var maxY = Math.Max(0d, (scaledSize.Height - viewportSize.Height) / 2d);

        var x = maxX <= 0 ? 0 : Math.Clamp(offset.X, -maxX, maxX);
        var y = maxY <= 0 ? 0 : Math.Clamp(offset.Y, -maxY, maxY);
        return new Vector(x, y);
    }
}
