using System.Windows;
using System.Windows.Controls;

namespace ArkFixYourQueues;

internal sealed class AspectRatioBorder : Border
{
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(double), typeof(AspectRatioBorder),
        new FrameworkPropertyMetadata(16d / 9d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (double.IsInfinity(constraint.Width) || constraint.Width <= 0 || AspectRatio <= 0)
            return base.MeasureOverride(constraint);

        var size = new Size(constraint.Width, constraint.Width / AspectRatio);
        base.MeasureOverride(size);
        return size;
    }
}
