using System.Buffers.Binary;
using System.Reflection;
using AGBModern;

namespace GBARenderer.Tests;

// These tests execute the embedded shader on Vulkan and read back authored video frames.
// A Vulkan-capable device is required; no ROM or captured game assets are used.
public sealed class ColorEffectsTests : IDisposable
{
    private readonly FrameRenderer _renderer = new();

    [Fact]
    public void ShadowBlendsWithTheCapturedWindowAndBlendRegisterSettings()
    {
        var frame = new TestFrame();
        frame.Register(0x00, 0x7F60); // Mode 0, BG0-3, OBJ, WIN0 and WIN1.
        frame.Register(0x48, 0x1F1F); // Visible layers, color effects disabled.
        frame.Register(0x50, 0x1E40); // Alpha, no ordinary first targets, BG1-3/OBJ second targets.

        AssertPixel(frame, 18, 18, 8);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void SemiTransparentObjectForcesAlphaRegardlessOfEffectSelection(int effect, bool effectsEnabled)
    {
        var frame = new TestFrame();
        frame.Register(0x48, effectsEnabled ? 0x3F : 0x1F);
        frame.Register(0x50, 0x0400 | (effect << 6)); // BG2 second target, no first targets.
        frame.Register(0x54, 16);

        AssertPixel(frame, 18, 18, 8);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NormalObjectRespectsTheWindowEffectsBit(int effect)
    {
        var frame = new TestFrame();
        frame.Object(0, semiTransparent: false);
        frame.Palette(257, 0x7A8A); // RGB5 (10, 20, 30).
        frame.Register(0x50, 0x0410 | (effect << 6));
        frame.Register(0x54, 16);

        AssertPixel(frame, 10, 20, 30);
    }

    [Fact]
    public void NormalObjectCanBlendWhenOrdinaryAlphaIsEnabled()
    {
        var frame = new TestFrame();
        frame.Object(0, semiTransparent: false);
        frame.Palette(257, 0x7A8A);
        frame.Register(0x48, 0x3F);
        frame.Register(0x50, 0x0450);

        AssertPixel(frame, 23, 28, 23);
    }

    [Theory]
    [InlineData(2, false, false, 10, 20, 30)]
    [InlineData(2, false, true, 10, 20, 30)]
    [InlineData(2, true, false, 10, 20, 30)]
    [InlineData(2, true, true, 20, 25, 30)]
    [InlineData(3, false, false, 10, 20, 30)]
    [InlineData(3, false, true, 10, 20, 30)]
    [InlineData(3, true, false, 10, 20, 30)]
    [InlineData(3, true, true, 5, 10, 15)]
    public void BrightnessFallbackRequiresWindowEffectsAndAnExplicitFirstTarget(
        int effect, bool effectsEnabled, bool firstTarget, int red, int green, int blue)
    {
        var frame = new TestFrame();
        frame.Palette(257, 0x7A8A);
        frame.Register(0x48, effectsEnabled ? 0x3F : 0x1F);
        frame.Register(0x50, (effect << 6) | (firstTarget ? 0x10 : 0)); // No second target.
        frame.Register(0x54, 8);

        AssertPixel(frame, red, green, blue);
    }

    [Fact]
    public void AlphaDoesNotSkipAnIneligibleImmediateBackground()
    {
        var frame = new TestFrame();
        frame.Register(0x50, 0x0800); // BG3 eligible, but BG2 is immediately under OBJ.

        AssertPixel(frame, 0, 0, 0);
    }

    [Fact]
    public void ShadowCanBlendWithBG3WhenBG2IsHidden()
    {
        var frame = new TestFrame();
        frame.Register(0x00, 0x3800); // OBJ, BG3 and WIN0.
        frame.Register(0x50, 0x0800);

        AssertPixel(frame, 12, 12, 13);
    }

    [Fact]
    public void AnOpaqueObjectOccludesTheShadowWithoutObjectToObjectBlending()
    {
        var frame = new TestFrame();
        frame.Object(0, semiTransparent: false, paletteBank: 1);
        frame.Object(1, semiTransparent: true);
        frame.Palette(273, 0x7A8A);
        frame.Register(0x50, 0x1000); // OBJ second target.

        AssertPixel(frame, 10, 20, 30);
    }

    [Fact]
    public void WindowObjectVisibilityStillHidesSemiTransparentObjects()
    {
        var frame = new TestFrame();
        frame.Register(0x48, 0x0F);

        AssertPixel(frame, 25, 24, 11);
    }

    [Theory]
    [InlineData(0x001F, 10, 20, 30)] // EVA clamps to 16.
    [InlineData(0x1F00, 25, 24, 11)] // EVB clamps to 16.
    [InlineData(0x1F1F, 31, 31, 31)] // Channels saturate at 31.
    public void ForcedAlphaRetainsFiveBitCoefficientClampingAndSaturation(
        int alpha, int red, int green, int blue)
    {
        var frame = new TestFrame();
        frame.Palette(257, 0x7A8A);
        frame.Register(0x52, alpha);

        AssertPixel(frame, red, green, blue);
    }

    private void AssertPixel(TestFrame frame, int red, int green, int blue)
    {
        _renderer.Submit(frame.Video, new FrameMotion(), new FrameMargins());
        _renderer.Render(FrameRenderer.Width, FrameRenderer.Height);
        var screenshot = Assert.IsType<Screenshot>(_renderer.TakeScreenshot());
        Assert.Equal(FrameRenderer.Width, screenshot.Width);
        Assert.Equal(FrameRenderer.Height, screenshot.Height);
        // Recover the 5-bit result so GPU UNORM byte rounding is outside this semantics test.
        var channels = screenshot.Pixels[..3].Select(channel => ((channel * 31) + 127) / 255).ToArray();
        Assert.Equal(new[] { red, green, blue }, channels);
        Assert.Equal(255, screenshot.Pixels[3]);
    }

    public void Dispose() => _renderer.Dispose();

    private sealed class TestFrame
    {
        public VideoFrame Video { get; } = new();
        private readonly byte[] _memory;

        public TestFrame()
        {
            // Author the existing frame backing store without adding a production mutation API.
            _memory = (byte[])typeof(VideoFrame).GetProperty("Copies", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Video)!;
            for (int n = 0; n < 128; n++)
            {
                Word(0x18400 + (n * 8), 0x0200); // Disable all unused OBJs.
            }

            _memory.AsSpan(32, 32).Fill(0x11); // BG2 tile 1, palette index 1.
            _memory.AsSpan(64, 32).Fill(0x22); // BG3 tile 2, palette index 2.
            _memory.AsSpan(0x10000, 32).Fill(0x11); // OBJ tile 0, palette index 1.
            Word(29 * 0x800, 1);
            Word(30 * 0x800, 2);
            Palette(1, 0x2F19); // RGB5 (25, 24, 11).
            Palette(2, 0x4A10); // RGB5 (16, 16, 18).
            Palette(257, 0);
            Object(0, semiTransparent: true);
            Register(0x00, 0x3C00); // Mode 0, OBJ, BG2, BG3 and WIN0.
            Register(0x0C, (29 << 8) | 2);
            Register(0x0E, (30 << 8) | 3);
            Register(0x40, 0x00F0);
            Register(0x44, 0x00A0);
            Register(0x48, 0x1F);
            Register(0x50, 0x0440);
            Register(0x52, 0x0C08);
        }

        public void Register(int offset, int value)
        {
            uint mask = 0xFFFFu << ((offset & 2) * 8);
            for (int line = 0; line < FrameRenderer.Height; line++)
            {
                ref uint word = ref Video.Lines[(line * VideoFrame.WordsPerLine) + (offset / 4)];
                word = (word & ~mask) | ((uint)value << ((offset & 2) * 8));
            }
        }

        public void Object(int index, bool semiTransparent, int paletteBank = 0)
        {
            int offset = 0x18400 + (index * 8);
            Word(offset, semiTransparent ? 0x0400 : 0);
            Word(offset + 2, 0); // 8x8 at (0, 0).
            Word(offset + 4, 0x0800 | (paletteBank << 12)); // Priority 2, tile 0.
        }

        public void Palette(int index, ushort color) => Word(0x18000 + (index * 2), color);

        private void Word(int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(_memory.AsSpan(offset), (ushort)value);
    }
}
