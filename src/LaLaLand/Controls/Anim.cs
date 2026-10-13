using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace LaLaLand.Controls;

/// <summary>
/// Thin helpers over Composition key-frame animations. These run on the compositor thread,
/// so they stay smooth even if the UI thread is busy.
/// </summary>
internal static class Anim
{
    private static CompositionEasingFunction? s_easeOut;
    private static CompositionEasingFunction? s_easeInOut;
    private static CompositionEasingFunction? s_linear;

    public static Visual Visual(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    public static Compositor Compositor(UIElement element) => Visual(element).Compositor;

    /// <summary>Smooth deceleration (similar to iOS' default curve).</summary>
    public static CompositionEasingFunction EaseOut(Compositor c) =>
        s_easeOut ??= c.CreateCubicBezierEasingFunction(new Vector2(0.22f, 0.9f), new Vector2(0.3f, 1f));

    public static CompositionEasingFunction EaseInOut(Compositor c) =>
        s_easeInOut ??= c.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.2f, 1f));

    public static CompositionEasingFunction Linear(Compositor c) => s_linear ??= c.CreateLinearEasingFunction();

    public static void SetOpacity(UIElement element, float value)
    {
        var v = Visual(element);
        v.StopAnimation("Opacity");
        v.Opacity = value;
    }

    public static void Fade(UIElement element, float to, double durationMs, double delayMs = 0, CompositionEasingFunction? easing = null)
    {
        var v = Visual(element);
        var c = v.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, to, easing ?? EaseOut(c));
        a.Duration = TimeSpan.FromMilliseconds(durationMs);
        a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Opacity", a);
    }

    public static void Scale(UIElement element, Vector3 to, double durationMs, double delayMs = 0, CompositionEasingFunction? easing = null, Vector3? from = null)
    {
        var v = Visual(element);
        var c = v.Compositor;
        var a = c.CreateVector3KeyFrameAnimation();
        if (from is { } f)
            a.InsertKeyFrame(0f, f);
        a.InsertKeyFrame(1f, to, easing ?? EaseOut(c));
        a.Duration = TimeSpan.FromMilliseconds(durationMs);
        a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Scale", a);
    }

    public static void EnableTranslation(UIElement element) => ElementCompositionPreview.SetIsTranslationEnabled(element, true);

    public static void Translate(UIElement element, Vector3 to, double durationMs, double delayMs = 0, CompositionEasingFunction? easing = null, Vector3? from = null)
    {
        var v = Visual(element);
        var c = v.Compositor;
        var a = c.CreateVector3KeyFrameAnimation();
        if (from is { } f)
            a.InsertKeyFrame(0f, f);
        a.InsertKeyFrame(1f, to, easing ?? EaseOut(c));
        a.Duration = TimeSpan.FromMilliseconds(durationMs);
        a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Translation", a);
    }

    public static void SetTranslation(UIElement element, Vector3 value)
    {
        var v = Visual(element);
        v.StopAnimation("Translation");
        v.Properties.InsertVector3("Translation", value);
    }
}
