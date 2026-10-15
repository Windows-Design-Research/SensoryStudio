using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SensoryStudio.WinUI.Services;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;

namespace SensoryStudio.WinUI.Experiences;

public sealed class PowderOrbExperience : UserControl, IExperience, IPausableExperience
{
    private const int ParticleCount = 5500;
    private const int DustCount = 130;
    private const double ReferenceMinDimension = 700;

    // Hit testing uses a fixed fraction of the control so breathing and press animation can
    // never move the boundary under a stationary cursor and fake an enter/exit transition.
    private const double HitRadiusFraction = 0.318;
    private const double BoundaryHysteresis = 1.04;

    // Hover texture, modelled on the Zen Garden rake: smoothed velocity drives jittered
    // distance-gated pulses that tighten up as the pointer speeds up.
    private const double HoverSpacingRadiusFraction = 0.14;
    private const double HoverSpacingJitter = 0.18;
    private const double HoverFastSpacingReduction = 0.30;
    private const double HoverFastSpeedDipsPerSecond = 900;
    private const double HoverSpeedSmoothing = 0.35;
    private const double MoveStopThresholdMs = 70;
    private const double MinPulseGapMs = 16;

    // Press and hold.
    private const double HoldActivationMs = 250;
    private const double HoldMoveToleranceDips = 8;
    private const double LeftHoldScale = 1.45;
    private const double RightPressScale = 0.62;
    private const double SpringStiffness = 160;
    private const double SmoothDamping = 26;
    private const double ReboundDamping = 7;
    private const double MaxSpringStepSeconds = 0.02;

    private readonly Image _image = new()
    {
        Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill,
    };

    // UserControl has no templated background border, so its own Background never
    // participates in hit testing. This transparent host guarantees the full control area
    // reports hover, including any letterboxed region the bitmap does not cover.
    private readonly Grid _hitTestHost = new()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(0, 0, 0, 0)),
    };
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _holdTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();
    private readonly float[] _particleX = new float[ParticleCount];
    private readonly float[] _particleY = new float[ParticleCount];
    private readonly float[] _particleZ = new float[ParticleCount];
    private readonly float[] _offsetX = new float[ParticleCount];
    private readonly float[] _offsetY = new float[ParticleCount];
    private readonly float[] _velocityX = new float[ParticleCount];
    private readonly float[] _velocityY = new float[ParticleCount];
    private readonly float[] _particleSize = new float[ParticleCount];
    private readonly float[] _phase = new float[ParticleCount];
    private readonly byte[] _colorIndex = new byte[ParticleCount];
    private readonly float[] _dustX = new float[DustCount];
    private readonly float[] _dustY = new float[DustCount];
    private readonly float[] _dustPhase = new float[DustCount];
    private readonly float[] _dustSize = new float[DustCount];
    private readonly int[][] _styles = new int[25][];

    private WriteableBitmap? _bitmap;
    private Stream? _pixelStream;
    private int[] _buffer = [];
    private byte[] _pixelBytes = [];
    private int _width;
    private int _height;
    private int _dustColor;
    private double _time;
    private long _lastFrameMs;
    private Point? _pointer;
    private Point? _burst;
    private bool _insideOrb;
    private bool _movingInside;
    private Point? _lastMovePosition;
    private double _accumulatedDistance;
    private double _lastMoveMs;
    private double _lastPulseMs;
    private double _smoothedSpeed;
    private double _nextPulseSpacing;
    private uint _spacingNoise = 0x9E3779B9;
    private double _hitRadius;

    private bool _leftPressed;
    private bool _rightPressed;
    private bool _leftHoldCandidate;
    private bool _leftHoldActive;
    private Point _leftPressOrigin;
    private double _scale = 1;
    private double _scaleTarget = 1;
    private double _scaleVelocity;
    private double _springDamping = SmoothDamping;

    public PowderOrbExperience()
    {
        _hitTestHost.Children.Add(_image);
        Content = _hitTestHost;
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(0, 0, 0, 0));
        IsTabStop = false;
        InitParticles();
        BuildStyles();

        SizeChanged += (_, _) => EnsureBitmap();
        PointerEntered += PointerEnteredHandler;
        PointerMoved += PointerMovedHandler;
        PointerPressed += PointerPressedHandler;
        PointerReleased += PointerReleasedHandler;
        PointerExited += PointerExitedHandler;
        PointerCanceled += PointerAbortHandler;
        PointerCaptureLost += PointerAbortHandler;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += Timer_Tick;

        _holdTimer = DispatcherQueue.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(HoldActivationMs);
        _holdTimer.IsRepeating = false;
        _holdTimer.Tick += HoldTimer_Tick;
    }

    public string Title => "Power Orb";
    public UserControl View => this;
    public bool IsPaused { get; private set; }

    public void Activate()
    {
        EnsureBitmap();
        _lastFrameMs = _clock.ElapsedMilliseconds;
        _timer.Start();
    }

    public void Deactivate()
    {
        _timer.Stop();
        AbortInteraction();
        _pointer = null;
        _insideOrb = false;
        _scale = 1;
        _scaleTarget = 1;
        _scaleVelocity = 0;
        ExperienceFeedback.Haptics.Stop();
    }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused)
        {
            _timer.Stop();
            AbortInteraction();
            ExperienceFeedback.Haptics.Stop();
        }
        else
        {
            _lastFrameMs = _clock.ElapsedMilliseconds;
            _timer.Start();
        }
    }

    private void PointerEnteredHandler(object sender, PointerRoutedEventArgs e)
    {
        ExperienceFeedback.Haptics.LogRouting("orb.entered", e.Pointer.PointerDeviceType);
        _pointer = e.GetCurrentPoint(this).Position;
        UpdateBoundary();
    }

    private void PointerMovedHandler(object sender, PointerRoutedEventArgs e)
    {
        Point position = e.GetCurrentPoint(this).Position;
        _pointer = position;
        UpdateHoldCandidate(position);

        // Entry/exit owns the event: never stack a Hover texture pulse on the same move.
        if (UpdateBoundary())
            return;

        UpdateMoveHaptics(position, _clock.Elapsed.TotalMilliseconds);
    }

    private void PointerPressedHandler(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        _pointer = point.Position;
        bool crossedBoundary = UpdateBoundary();
        ExperienceFeedback.Haptics.LogRouting("orb.pressed", e.Pointer.PointerDeviceType);

        PointerPointProperties properties = point.Properties;
        if (properties.IsLeftButtonPressed && !_leftPressed)
        {
            _leftPressed = true;
            _leftHoldActive = false;
            _leftHoldCandidate = true;
            _leftPressOrigin = point.Position;
            _burst = point.Position;
            _holdTimer.Start();

            // Fires on the same frame as the burst so the felt and seen events line up.
            // Suppressed when this press also crossed the boundary, so the entry Grow is
            // never stacked with a Click in one event.
            if (_insideOrb && !crossedBoundary)
                ExperienceFeedback.Haptics.SendExact(HapticsType.Click);
        }

        if (properties.IsRightButtonPressed && !_rightPressed)
        {
            _rightPressed = true;
            ApplyScaleState(SmoothDamping);
            ExperienceFeedback.Haptics.Send(HapticsType.Grow);
        }

        if (_leftPressed || _rightPressed)
            CapturePointer(e.Pointer);
    }

    private void PointerReleasedHandler(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        _pointer = point.Position;
        PointerPointProperties properties = point.Properties;

        if (_leftPressed && !properties.IsLeftButtonPressed)
            ReleaseLeft();

        if (_rightPressed && !properties.IsRightButtonPressed)
            ReleaseRight();

        if (!_leftPressed && !_rightPressed)
            ReleasePointerCapture(e.Pointer);

        UpdateBoundary();
    }

    private void PointerExitedHandler(object sender, PointerRoutedEventArgs e)
    {
        // While a button is held the pointer is captured, so an exit is not a real boundary
        // crossing and must not tear down the press interaction.
        if (_leftPressed || _rightPressed)
            return;

        _pointer = null;
        UpdateBoundary();
        ResetMoveTracking();
    }

    private void PointerAbortHandler(object sender, PointerRoutedEventArgs e)
    {
        // Releasing capture ourselves also raises PointerCaptureLost; only a genuinely
        // interrupted press should tear the interaction down.
        if (!_leftPressed && !_rightPressed)
            return;

        AbortInteraction();
    }

    private void HoldTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_leftPressed || !_leftHoldCandidate || _leftHoldActive)
            return;

        _leftHoldActive = true;
        ApplyScaleState(SmoothDamping);
        ExperienceFeedback.Haptics.Send(HapticsType.Grow);
    }

    private void UpdateHoldCandidate(Point position)
    {
        if (!_leftHoldCandidate || _leftHoldActive)
            return;

        if (Square(position.X - _leftPressOrigin.X) + Square(position.Y - _leftPressOrigin.Y) <=
            HoldMoveToleranceDips * HoldMoveToleranceDips)
            return;

        _leftHoldCandidate = false;
        _holdTimer.Stop();
    }

    private void ReleaseLeft()
    {
        _leftPressed = false;
        _leftHoldCandidate = false;
        _holdTimer.Stop();
        if (!_leftHoldActive)
            return;

        _leftHoldActive = false;
        ApplyScaleState(SmoothDamping);
        ExperienceFeedback.Haptics.Send(HapticsType.Grow);
    }

    private void ReleaseRight()
    {
        _rightPressed = false;
        // Underdamped return so the orb visually overshoots its resting size before settling.
        ApplyScaleState(ReboundDamping);
        ExperienceFeedback.Haptics.Send(HapticsType.Grow);
    }

    private void AbortInteraction()
    {
        _holdTimer.Stop();
        _leftPressed = false;
        _rightPressed = false;
        _leftHoldCandidate = false;
        _leftHoldActive = false;
        ResetMoveTracking();
        ApplyScaleState(SmoothDamping);
    }

    /// <summary>
    /// Derives the resting scale from the current press state so overlapping left-hold and
    /// right-press interactions cannot leave the orb stuck at the wrong size.
    /// </summary>
    private void ApplyScaleState(double damping)
    {
        _scaleTarget = _rightPressed
            ? RightPressScale
            : _leftHoldActive
                ? LeftHoldScale
                : 1;
        _springDamping = damping;
    }

    private void ResetMoveTracking()
    {
        _movingInside = false;
        _lastMovePosition = _pointer;
        _accumulatedDistance = 0;
        _smoothedSpeed = 0;
        _nextPulseSpacing = 0;
    }

    private void UpdateMoveHaptics(Point position, double nowMs)
    {
        if (!_insideOrb)
            return;

        if (!_movingInside || nowMs - _lastMoveMs >= MoveStopThresholdMs)
        {
            _movingInside = true;
            _lastMovePosition = position;
            _lastMoveMs = nowMs;
            _accumulatedDistance = 0;
            _smoothedSpeed = 0;
            _nextPulseSpacing = 0;
            return;
        }

        Point previous = _lastMovePosition ?? position;
        double distance = Math.Sqrt(
            Square(position.X - previous.X) + Square(position.Y - previous.Y));
        double seconds = Math.Max((nowMs - _lastMoveMs) / 1000.0, 0.001);
        _lastMovePosition = position;
        _lastMoveMs = nowMs;
        if (distance <= 0)
            return;

        _smoothedSpeed += ((distance / seconds) - _smoothedSpeed) * HoverSpeedSmoothing;
        double speedNormalized = Math.Clamp(_smoothedSpeed / HoverFastSpeedDipsPerSecond, 0, 1);
        double fastFill = Math.Clamp((speedNormalized - 0.55) / 0.45, 0, 1);

        if (_nextPulseSpacing <= 0)
            _nextPulseSpacing = NextPulseSpacing(fastFill);

        _accumulatedDistance = Math.Min(
            _accumulatedDistance + distance,
            _nextPulseSpacing * 2);

        if (_accumulatedDistance < _nextPulseSpacing || nowMs - _lastPulseMs < MinPulseGapMs)
            return;

        _accumulatedDistance -= _nextPulseSpacing;
        _nextPulseSpacing = NextPulseSpacing(fastFill);
        _lastPulseMs = nowMs;
        ExperienceFeedback.Haptics.SendTextured(
            HapticsType.Hover,
            Math.Min(0.72 + (speedNormalized * 0.12) + (fastFill * 0.10), 0.94));
    }

    private double NextPulseSpacing(double fastFill)
    {
        _spacingNoise = (_spacingNoise * 1664525u) + 1013904223u;
        double unit = _spacingNoise / (double)uint.MaxValue;
        double jitter = 1 + (((unit * 2) - 1) * HoverSpacingJitter);
        double baseSpacing = Math.Max(_hitRadius * HoverSpacingRadiusFraction, 2);
        return baseSpacing * (1 - (fastFill * HoverFastSpacingReduction)) * jitter;
    }

    /// <summary>Returns true when this call crossed the orb boundary and emitted a pulse.</summary>
    private bool UpdateBoundary()
    {
        if (_hitRadius <= 0)
            return false;

        bool inside = _insideOrb;
        if (_pointer is not Point point)
        {
            inside = false;
        }
        else
        {
            // Asymmetric thresholds keep a cursor parked on the rim from chattering
            // Grow/Collide at the device.
            double distanceSquared =
                Square(point.X - (_width * 0.5)) + Square(point.Y - (_height * 0.5));
            double enterRadius = _hitRadius;
            double exitRadius = _hitRadius * BoundaryHysteresis;
            if (!_insideOrb && distanceSquared <= enterRadius * enterRadius)
                inside = true;
            else if (_insideOrb && distanceSquared > exitRadius * exitRadius)
                inside = false;
        }

        if (inside == _insideOrb)
            return false;

        _insideOrb = inside;
        ResetMoveTracking();
        ExperienceFeedback.Haptics.Send(inside ? HapticsType.Grow : HapticsType.Collide);
        return true;
    }

    private void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        EnsureBitmap();
        if (_bitmap == null || _pixelStream == null || _buffer.Length == 0)
            return;

        long now = _clock.ElapsedMilliseconds;
        double deltaSeconds = Math.Clamp((now - _lastFrameMs) / 1000.0, 0, 0.05);
        _lastFrameMs = now;
        _time += deltaSeconds;

        UpdateScale(deltaSeconds);
        RenderFrame();

        Buffer.BlockCopy(_buffer, 0, _pixelBytes, 0, _pixelBytes.Length);
        _pixelStream.Position = 0;
        _pixelStream.Write(_pixelBytes, 0, _pixelBytes.Length);
        _bitmap.Invalidate();
    }

    private void UpdateScale(double deltaSeconds)
    {
        double step = Math.Min(deltaSeconds, MaxSpringStepSeconds);
        if (step <= 0)
            return;

        double acceleration =
            ((_scaleTarget - _scale) * SpringStiffness) - (_scaleVelocity * _springDamping);
        _scaleVelocity += acceleration * step;
        _scale += _scaleVelocity * step;

        if (Math.Abs(_scaleTarget - _scale) < 0.0005 && Math.Abs(_scaleVelocity) < 0.005)
        {
            _scale = _scaleTarget;
            _scaleVelocity = 0;
            _springDamping = SmoothDamping;
        }
    }

    private void EnsureBitmap()
    {
        int width = (int)Math.Round(ActualWidth);
        int height = (int)Math.Round(ActualHeight);
        if (width < 2 || height < 2 || (_bitmap != null && width == _width && height == _height))
            return;

        _pixelStream?.Dispose();
        _width = width;
        _height = height;
        _hitRadius = Math.Min(width, height) * HitRadiusFraction;
        _buffer = new int[width * height];
        _pixelBytes = new byte[_buffer.Length * sizeof(int)];
        _bitmap = new WriteableBitmap(width, height);
        _pixelStream = _bitmap.PixelBuffer.AsStream();
        _image.Source = _bitmap;
    }

    private void RenderFrame()
    {
        Array.Fill(_buffer, 0);
        double sizeScale = Math.Clamp(Math.Min(_width, _height) / ReferenceMinDimension, 0.3, 1);

        for (int index = 0; index < DustCount; index++)
        {
            double x = (_dustX[index] * _width) + (Math.Sin((_time * 0.3) + _dustPhase[index]) * 18);
            double y = (_dustY[index] * _height) + (Math.Cos((_time * 0.23) + (_dustPhase[index] * 1.7)) * 14);
            Plot(x, y, _dustSize[index] * sizeScale, _dustColor);
        }

        double rotation = _time * 0.22;
        double cosRotation = Math.Cos(rotation);
        double sinRotation = Math.Sin(rotation);
        const double tilt = 0.35;
        double cosTilt = Math.Cos(tilt);
        double sinTilt = Math.Sin(tilt);
        double centerX = _width / 2.0;
        double centerY = _height / 2.0;
        double breath = 0.5 - (0.5 * Math.Cos(_time * 0.5));
        double radius = Math.Min(_width, _height) * 0.3 * (1 + (0.12 * breath)) * _scale;

        for (int index = 0; index < ParticleCount; index++)
        {
            double x = _particleX[index];
            double y = _particleY[index] + (0.012 * Math.Sin((_time * 1.7) + _phase[index]));
            double z = _particleZ[index];
            double rotatedX = (x * cosRotation) + (z * sinRotation);
            double rotatedZ = (z * cosRotation) - (x * sinRotation);
            double rotatedY = (y * cosTilt) - (rotatedZ * sinTilt);
            double depth = (y * sinTilt) + (rotatedZ * cosTilt);

            _velocityX[index] += -0.06f * _offsetX[index];
            _velocityY[index] += -0.06f * _offsetY[index];
            _velocityX[index] *= 0.9f;
            _velocityY[index] *= 0.9f;

            double screenX = centerX + (rotatedX * radius) + _offsetX[index];
            double screenY = centerY + (rotatedY * radius) + _offsetY[index];
            ApplyPointerForce(index, screenX, screenY, _pointer, 90, 1.9);
            ApplyPointerForce(index, screenX, screenY, _burst, 160, 13);
            _offsetX[index] += _velocityX[index];
            _offsetY[index] += _velocityY[index];

            int depthBucket = Math.Clamp((int)((depth + 1) * 3.99), 0, 7);
            double size = _particleSize[index] * sizeScale * (0.65 + (0.35 * (depth + 1) / 2));
            Plot(screenX, screenY, size, _styles[_colorIndex[index]][depthBucket]);
        }

        _burst = null;
    }

    private void ApplyPointerForce(
        int index,
        double particleX,
        double particleY,
        Point? source,
        double range,
        double strength)
    {
        if (source is not Point point)
            return;

        double dx = particleX - point.X;
        double dy = particleY - point.Y;
        double distanceSquared = (dx * dx) + (dy * dy);
        if (distanceSquared >= range * range)
            return;

        double distance = Math.Max(Math.Sqrt(distanceSquared), 0.000001);
        double force = ((range - distance) / range) * strength;
        _velocityX[index] += (float)((dx / distance) * force);
        _velocityY[index] += (float)((dy / distance) * force);
    }

    private void Plot(double x, double y, double size, int color)
    {
        int pixelSize = Math.Max(1, (int)Math.Round(size));
        int startX = (int)Math.Round(x);
        int startY = (int)Math.Round(y);
        for (int row = 0; row < pixelSize; row++)
        {
            int pixelY = startY + row;
            if (pixelY < 0 || pixelY >= _height)
                continue;
            int rowOffset = pixelY * _width;
            for (int column = 0; column < pixelSize; column++)
            {
                int pixelX = startX + column;
                if (pixelX >= 0 && pixelX < _width)
                    _buffer[rowOffset + pixelX] = color;
            }
        }
    }

    private float Gaussian()
    {
        double sum = 0;
        for (int index = 0; index < 4; index++)
            sum += _random.NextDouble();
        return (float)(sum - 2);
    }

    private void InitParticles()
    {
        for (int index = 0; index < ParticleCount; index++)
        {
            double x = Gaussian();
            double y = Gaussian();
            double z = Gaussian();
            double length = Math.Max(Math.Sqrt((x * x) + (y * y) + (z * z)), 0.000001);
            double radiusFactor = 1 - (0.3 * _random.NextDouble() * _random.NextDouble());
            _particleX[index] = (float)((x / length) * radiusFactor);
            _particleY[index] = (float)((y / length) * radiusFactor);
            _particleZ[index] = (float)((z / length) * radiusFactor);
            _particleSize[index] = (float)(1.1 + (1.5 * _random.NextDouble()));
            _phase[index] = (float)(_random.NextDouble() * Math.Tau);
            double latitude = (_particleY[index] + 1) / 2;
            _colorIndex[index] = (byte)(_random.NextDouble() < 0.07
                ? 24
                : Math.Min(23, (int)(latitude * 24)));
        }

        for (int index = 0; index < DustCount; index++)
        {
            _dustX[index] = (float)_random.NextDouble();
            _dustY[index] = (float)_random.NextDouble();
            _dustPhase[index] = (float)(_random.NextDouble() * Math.Tau);
            _dustSize[index] = (float)(0.8 + (1.2 * _random.NextDouble()));
        }
    }

    private void BuildStyles()
    {
        for (int colorIndex = 0; colorIndex <= 24; colorIndex++)
        {
            (int red, int green, int blue) = colorIndex == 24
                ? HslToRgb(178, 0.6, 0.4)
                : HslToRgb(335 + ((colorIndex / 23.0) * 55), 0.72, 0.55);
            var depthStyles = new int[8];
            for (int depth = 0; depth < depthStyles.Length; depth++)
            {
                double normalizedDepth = (depth / 7.0 * 2) - 1;
                double fade = ((1 - normalizedDepth) / 2) * 0.68;
                int r = (int)Math.Round((red * (1 - fade)) + (255 * fade));
                int g = (int)Math.Round((green * (1 - fade)) + (255 * fade));
                int b = (int)Math.Round((blue * (1 - fade)) + (255 * fade));
                depthStyles[depth] = BlendOverWhite(r, g, b, 0.9);
            }
            _styles[colorIndex] = depthStyles;
        }
        _dustColor = BlendOverWhite(168, 150, 138, 0.35);
    }

    private static int BlendOverWhite(int red, int green, int blue, double alpha)
    {
        int r = (int)Math.Round((red * alpha) + (255 * (1 - alpha)));
        int g = (int)Math.Round((green * alpha) + (255 * (1 - alpha)));
        int b = (int)Math.Round((blue * alpha) + (255 * (1 - alpha)));
        return unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
    }

    private static (int R, int G, int B) HslToRgb(double hue, double saturation, double lightness)
    {
        hue = ((hue % 360) + 360) % 360;
        double chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        double intermediate = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
        double offset = lightness - (chroma / 2);
        double red = 0;
        double green = 0;
        double blue = 0;
        if (hue < 60) { red = chroma; green = intermediate; }
        else if (hue < 120) { red = intermediate; green = chroma; }
        else if (hue < 180) { green = chroma; blue = intermediate; }
        else if (hue < 240) { green = intermediate; blue = chroma; }
        else if (hue < 300) { red = intermediate; blue = chroma; }
        else { red = chroma; blue = intermediate; }
        return (
            (int)Math.Round((red + offset) * 255),
            (int)Math.Round((green + offset) * 255),
            (int)Math.Round((blue + offset) * 255));
    }

    private static double Square(double value) => value * value;
}
