using System.Threading;

namespace SRN.CC.App.Views;

/// <summary>
/// App-lifetime backstop for architecture decision A4's "only for visible 3D slots" rule. Avalonia's
/// <c>OpenGlControlBase</c> still constructs a control and still creates a GL context on first attach
/// even when <c>IsVisible</c> is false, so "only for visible slots" cannot be enforced with an
/// <c>IsVisible</c> check alone — the comparison panel already limits successful model previews to at
/// most three slots, but a fourth concurrent viewport (e.g. transient states during a slot switch)
/// must still be refused structurally. <see cref="ModelViewportControl"/> calls
/// <see cref="TryAcquire"/> from <c>OnAttachedToVisualTree</c> and <see cref="Release"/> from
/// <c>OnDetachedFromVisualTree</c>; a refused acquire means the control must not attempt any GPU work
/// in <c>OnOpenGlInit</c> and instead reports render-unavailability so the slot degrades to text.
/// </summary>
/// <remarks>
/// Deliberately a plain, instantiable class — not a hard-coded static singleton — so tests can create
/// isolated instances instead of sharing mutable counter state across test runs.
/// <see cref="ModelViewportControl.SharedRegistry"/> holds the one instance the control actually uses
/// at app lifetime, and it is a public, settable static precisely so a headless test's <c>[SetUp]</c>
/// can swap in a fresh <see cref="ModelViewportRegistry"/> before each test.
/// </remarks>
public sealed class ModelViewportRegistry
{
    /// <summary>Matches the comparison panel's fixed three-slot layout.</summary>
    public const int MaxConcurrent = 3;

    private int _activeCount;

    /// <summary>Number of viewports currently holding an acquired slot. Thread-safe to read.</summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>
    /// Attempts to reserve one of <see cref="MaxConcurrent"/> slots. Thread-safe via a
    /// compare-exchange loop (Avalonia visual-tree attach/detach callbacks are expected on the UI
    /// thread, but this makes no such assumption).
    /// </summary>
    /// <returns>True when a slot was reserved; false when all <see cref="MaxConcurrent"/> slots are already in use.</returns>
    public bool TryAcquire()
    {
        while (true)
        {
            int current = Volatile.Read(ref _activeCount);
            if (current >= MaxConcurrent)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Releases a previously reserved slot. Safe to call even if <see cref="ActiveCount"/> is already
    /// zero (a no-op) — callers only invoke this after a successful <see cref="TryAcquire"/>, but this
    /// keeps the counter from ever going negative regardless.
    /// </summary>
    public void Release()
    {
        while (true)
        {
            int current = Volatile.Read(ref _activeCount);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _activeCount, current - 1, current) == current)
            {
                return;
            }
        }
    }
}
