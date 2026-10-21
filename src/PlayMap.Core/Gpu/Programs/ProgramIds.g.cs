// The product programs' constants and textures by their place in the program table (programs.layout.json), so a draw sets
// them with no look-up by name. ProgramTableTests writes this file from the table (PLAYMAP_WRITE_FIXTURES=1); it changes
// only with the table.

namespace PlayMap.Core.Gpu.Programs;

/// <summary>Each product program's id, its constants' ids and its textures' ids, as the program table numbers them.</summary>
public static class ProgramIds
{
    /// <summary>The <c>present</c> program.</summary>
    public static class Present
    {
        public static readonly ProgramId Program = new(0);
        public static readonly TextureId uTex = new(Program, 0, "uTex");
    }

    /// <summary>The <c>blit</c> program.</summary>
    public static class Blit
    {
        public static readonly ProgramId Program = new(1);
        public static readonly ConstantId uSrc = new(Program, 0, "uSrc");
        public static readonly TextureId uTex = new(Program, 0, "uTex");
    }

    /// <summary>The <c>solid</c> program.</summary>
    public static class Solid
    {
        public static readonly ProgramId Program = new(2);
        public static readonly ConstantId uColor = new(Program, 0, "uColor");
    }

    /// <summary>The <c>quad</c> program.</summary>
    public static class Quad
    {
        public static readonly ProgramId Program = new(3);
        public static readonly ConstantId uRect = new(Program, 0, "uRect");
        public static readonly ConstantId uTarget = new(Program, 1, "uTarget");
        public static readonly ConstantId uUv = new(Program, 2, "uUv");
        public static readonly ConstantId uOpacity = new(Program, 3, "uOpacity");
        public static readonly ConstantId uMode = new(Program, 4, "uMode");
        public static readonly ConstantId uTint = new(Program, 5, "uTint");
        public static readonly TextureId uTex = new(Program, 0, "uTex");
    }

    /// <summary>The <c>mix</c> program.</summary>
    public static class Mix
    {
        public static readonly ProgramId Program = new(4);
        public static readonly ConstantId uWeights = new(Program, 0, "uWeights");
        public static readonly ConstantId uGamma = new(Program, 1, "uGamma");
        public static readonly TextureId uFrom = new(Program, 0, "uFrom");
        public static readonly TextureId uTo = new(Program, 1, "uTo");
    }

    /// <summary>The <c>yuv</c> program.</summary>
    public static class Yuv
    {
        public static readonly ProgramId Program = new(5);
        public static readonly ConstantId uPlanes = new(Program, 0, "uPlanes");
        public static readonly ConstantId uCodeScale = new(Program, 1, "uCodeScale");
        public static readonly ConstantId uRange = new(Program, 2, "uRange");
        public static readonly ConstantId uMatrix = new(Program, 3, "uMatrix");
        public static readonly ConstantId uTransfer = new(Program, 4, "uTransfer");
        public static readonly ConstantId uWide = new(Program, 5, "uWide");
        public static readonly ConstantId uCrop = new(Program, 6, "uCrop");
        public static readonly ConstantId uMaxUv = new(Program, 7, "uMaxUv");
        public static readonly TextureId uY = new(Program, 0, "uY");
        public static readonly TextureId uU = new(Program, 1, "uU");
        public static readonly TextureId uV = new(Program, 2, "uV");
    }

    /// <summary>The <c>stage</c> program.</summary>
    public static class Stage
    {
        public static readonly ProgramId Program = new(6);
        public static readonly ConstantId uModel = new(Program, 0, "uModel");
        public static readonly ConstantId uViewProj = new(Program, 1, "uViewProj");
        public static readonly ConstantId uNormalMat = new(Program, 2, "uNormalMat");
        public static readonly ConstantId uProjector = new(Program, 3, "uProjector");
        public static readonly ConstantId uMode = new(Program, 4, "uMode");
        public static readonly ConstantId uRegion = new(Program, 5, "uRegion");
        public static readonly ConstantId uHasMask = new(Program, 6, "uHasMask");
        public static readonly ConstantId uOpacity = new(Program, 7, "uOpacity");
        public static readonly ConstantId uColorA = new(Program, 8, "uColorA");
        public static readonly ConstantId uColorB = new(Program, 9, "uColorB");
        public static readonly ConstantId uParams = new(Program, 10, "uParams");
        public static readonly ConstantId uTime = new(Program, 11, "uTime");
        public static readonly ConstantId uBoundsMin = new(Program, 12, "uBoundsMin");
        public static readonly ConstantId uBoundsMax = new(Program, 13, "uBoundsMax");
        public static readonly ConstantId uEye = new(Program, 14, "uEye");
        public static readonly TextureId uSource = new(Program, 0, "uSource");
        public static readonly TextureId uMask = new(Program, 1, "uMask");
    }

    /// <summary>The <c>output</c> program.</summary>
    public static class Output
    {
        public static readonly ProgramId Program = new(7);
        public static readonly ConstantId uRotation = new(Program, 0, "uRotation");
        public static readonly ConstantId uSrc = new(Program, 1, "uSrc");
        public static readonly ConstantId uHasLens = new(Program, 2, "uHasLens");
        public static readonly ConstantId uHasMask = new(Program, 3, "uHasMask");
        public static readonly ConstantId uHasBlend = new(Program, 4, "uHasBlend");
        public static readonly ConstantId uGain = new(Program, 5, "uGain");
        public static readonly ConstantId uTone = new(Program, 6, "uTone");
        public static readonly ConstantId uFrame = new(Program, 7, "uFrame");
        public static readonly ConstantId uAir = new(Program, 8, "uAir");
        public static readonly ConstantId uHasMeasuredGrid = new(Program, 9, "uHasMeasuredGrid");
        public static readonly ConstantId uHasMeasuredBlend = new(Program, 10, "uHasMeasuredBlend");
        public static readonly ConstantId uMatch = new(Program, 11, "uMatch");
        public static readonly ConstantId uMeasuredGains = new(Program, 12, "uMeasuredGains");
        public static readonly ConstantId uMeasuredGamma = new(Program, 13, "uMeasuredGamma");
        public static readonly ConstantId uMeasuredPedestal = new(Program, 14, "uMeasuredPedestal");
        public static readonly TextureId uPicture = new(Program, 0, "uPicture");
        public static readonly TextureId uLens = new(Program, 1, "uLens");
        public static readonly TextureId uMask = new(Program, 2, "uMask");
        public static readonly TextureId uBlend = new(Program, 3, "uBlend");
        public static readonly TextureId uMeasuredGrid = new(Program, 4, "uMeasuredGrid");
        public static readonly TextureId uMeasuredBlend = new(Program, 5, "uMeasuredBlend");
    }

    /// <summary>The <c>sprite</c> program.</summary>
    public static class Sprite
    {
        public static readonly ProgramId Program = new(8);
        public static readonly ConstantId uTarget = new(Program, 0, "uTarget");
        public static readonly ConstantId uAtlas = new(Program, 1, "uAtlas");
        public static readonly ConstantId uOffset = new(Program, 2, "uOffset");
        public static readonly TextureId uTex = new(Program, 0, "uTex");
    }

    /// <summary>The <c>lines</c> program.</summary>
    public static class Lines
    {
        public static readonly ProgramId Program = new(9);
        public static readonly ConstantId uViewProj = new(Program, 0, "uViewProj");
    }

    /// <summary>The <c>checker</c> program.</summary>
    public static class Checker
    {
        public static readonly ProgramId Program = new(10);
        public static readonly ConstantId uCell = new(Program, 0, "uCell");
        public static readonly ConstantId uColorA = new(Program, 1, "uColorA");
        public static readonly ConstantId uColorB = new(Program, 2, "uColorB");
        public static readonly ConstantId uSwap = new(Program, 3, "uSwap");
    }

    /// <summary>The <c>motion</c> program.</summary>
    public static class Motion
    {
        public static readonly ProgramId Program = new(11);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uVariant = new(Program, 1, "uVariant");
        public static readonly ConstantId uTime = new(Program, 2, "uTime");
        public static readonly ConstantId uFrame = new(Program, 3, "uFrame");
        public static readonly ConstantId uSpeed = new(Program, 4, "uSpeed");
        public static readonly ConstantId uPxPerFrame = new(Program, 5, "uPxPerFrame");
        public static readonly ConstantId uVertical = new(Program, 6, "uVertical");
        public static readonly ConstantId uAccent = new(Program, 7, "uAccent");
        public static readonly ConstantId uFlash = new(Program, 8, "uFlash");
    }

    /// <summary>The <c>colorcycle</c> program.</summary>
    public static class Colorcycle
    {
        public static readonly ProgramId Program = new(12);
        public static readonly ConstantId uColors = new(Program, 0, "uColors");
        public static readonly ConstantId uCount = new(Program, 1, "uCount");
        public static readonly ConstantId uTime = new(Program, 2, "uTime");
        public static readonly ConstantId uInterval = new(Program, 3, "uInterval");
        public static readonly ConstantId uFade = new(Program, 4, "uFade");
    }

    /// <summary>The <c>syncmarks</c> program.</summary>
    public static class Syncmarks
    {
        public static readonly ProgramId Program = new(13);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uLevel = new(Program, 1, "uLevel");
        public static readonly ConstantId uPhase = new(Program, 2, "uPhase");
    }

    /// <summary>The <c>fractal.mandelbrot</c> program.</summary>
    public static class FractalMandelbrot
    {
        public static readonly ProgramId Program = new(14);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uCenter = new(Program, 1, "uCenter");
        public static readonly ConstantId uUpp = new(Program, 2, "uUpp");
        public static readonly ConstantId uC = new(Program, 3, "uC");
        public static readonly ConstantId uIters = new(Program, 4, "uIters");
        public static readonly ConstantId uOffset = new(Program, 5, "uOffset");
        public static readonly ConstantId uBright = new(Program, 6, "uBright");
        public static readonly ConstantId uNcol = new(Program, 7, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 8, "uP0");
        public static readonly ConstantId uP1 = new(Program, 9, "uP1");
        public static readonly ConstantId uP2 = new(Program, 10, "uP2");
        public static readonly ConstantId uP3 = new(Program, 11, "uP3");
        public static readonly ConstantId uP4 = new(Program, 12, "uP4");
        public static readonly ConstantId uT = new(Program, 13, "uT");
        public static readonly ConstantId uRot = new(Program, 14, "uRot");
        public static readonly ConstantId uWarp = new(Program, 15, "uWarp");
    }

    /// <summary>The <c>fractal.julia</c> program.</summary>
    public static class FractalJulia
    {
        public static readonly ProgramId Program = new(15);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uCenter = new(Program, 1, "uCenter");
        public static readonly ConstantId uUpp = new(Program, 2, "uUpp");
        public static readonly ConstantId uC = new(Program, 3, "uC");
        public static readonly ConstantId uIters = new(Program, 4, "uIters");
        public static readonly ConstantId uOffset = new(Program, 5, "uOffset");
        public static readonly ConstantId uBright = new(Program, 6, "uBright");
        public static readonly ConstantId uNcol = new(Program, 7, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 8, "uP0");
        public static readonly ConstantId uP1 = new(Program, 9, "uP1");
        public static readonly ConstantId uP2 = new(Program, 10, "uP2");
        public static readonly ConstantId uP3 = new(Program, 11, "uP3");
        public static readonly ConstantId uP4 = new(Program, 12, "uP4");
        public static readonly ConstantId uT = new(Program, 13, "uT");
        public static readonly ConstantId uRot = new(Program, 14, "uRot");
        public static readonly ConstantId uWarp = new(Program, 15, "uWarp");
    }

    /// <summary>The <c>fractal.burningship</c> program.</summary>
    public static class FractalBurningship
    {
        public static readonly ProgramId Program = new(16);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uCenter = new(Program, 1, "uCenter");
        public static readonly ConstantId uUpp = new(Program, 2, "uUpp");
        public static readonly ConstantId uC = new(Program, 3, "uC");
        public static readonly ConstantId uIters = new(Program, 4, "uIters");
        public static readonly ConstantId uOffset = new(Program, 5, "uOffset");
        public static readonly ConstantId uBright = new(Program, 6, "uBright");
        public static readonly ConstantId uNcol = new(Program, 7, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 8, "uP0");
        public static readonly ConstantId uP1 = new(Program, 9, "uP1");
        public static readonly ConstantId uP2 = new(Program, 10, "uP2");
        public static readonly ConstantId uP3 = new(Program, 11, "uP3");
        public static readonly ConstantId uP4 = new(Program, 12, "uP4");
        public static readonly ConstantId uT = new(Program, 13, "uT");
        public static readonly ConstantId uRot = new(Program, 14, "uRot");
        public static readonly ConstantId uWarp = new(Program, 15, "uWarp");
    }

    /// <summary>The <c>fractal.newton</c> program.</summary>
    public static class FractalNewton
    {
        public static readonly ProgramId Program = new(17);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uCenter = new(Program, 1, "uCenter");
        public static readonly ConstantId uUpp = new(Program, 2, "uUpp");
        public static readonly ConstantId uC = new(Program, 3, "uC");
        public static readonly ConstantId uIters = new(Program, 4, "uIters");
        public static readonly ConstantId uOffset = new(Program, 5, "uOffset");
        public static readonly ConstantId uBright = new(Program, 6, "uBright");
        public static readonly ConstantId uNcol = new(Program, 7, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 8, "uP0");
        public static readonly ConstantId uP1 = new(Program, 9, "uP1");
        public static readonly ConstantId uP2 = new(Program, 10, "uP2");
        public static readonly ConstantId uP3 = new(Program, 11, "uP3");
        public static readonly ConstantId uP4 = new(Program, 12, "uP4");
        public static readonly ConstantId uT = new(Program, 13, "uT");
        public static readonly ConstantId uRot = new(Program, 14, "uRot");
        public static readonly ConstantId uWarp = new(Program, 15, "uWarp");
    }

    /// <summary>The <c>fractal.warp</c> program.</summary>
    public static class FractalWarp
    {
        public static readonly ProgramId Program = new(18);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uCenter = new(Program, 1, "uCenter");
        public static readonly ConstantId uUpp = new(Program, 2, "uUpp");
        public static readonly ConstantId uC = new(Program, 3, "uC");
        public static readonly ConstantId uIters = new(Program, 4, "uIters");
        public static readonly ConstantId uOffset = new(Program, 5, "uOffset");
        public static readonly ConstantId uBright = new(Program, 6, "uBright");
        public static readonly ConstantId uNcol = new(Program, 7, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 8, "uP0");
        public static readonly ConstantId uP1 = new(Program, 9, "uP1");
        public static readonly ConstantId uP2 = new(Program, 10, "uP2");
        public static readonly ConstantId uP3 = new(Program, 11, "uP3");
        public static readonly ConstantId uP4 = new(Program, 12, "uP4");
        public static readonly ConstantId uT = new(Program, 13, "uT");
        public static readonly ConstantId uRot = new(Program, 14, "uRot");
        public static readonly ConstantId uWarp = new(Program, 15, "uWarp");
    }

    /// <summary>The <c>reactive.plasma</c> program.</summary>
    public static class ReactivePlasma
    {
        public static readonly ProgramId Program = new(19);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uT = new(Program, 1, "uT");
        public static readonly ConstantId uRot = new(Program, 2, "uRot");
        public static readonly ConstantId uDepth = new(Program, 3, "uDepth");
        public static readonly ConstantId uSym = new(Program, 4, "uSym");
        public static readonly ConstantId uLow = new(Program, 5, "uLow");
        public static readonly ConstantId uAmount = new(Program, 6, "uAmount");
        public static readonly ConstantId uBright = new(Program, 7, "uBright");
        public static readonly ConstantId uNcol = new(Program, 8, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 9, "uP0");
        public static readonly ConstantId uP1 = new(Program, 10, "uP1");
        public static readonly ConstantId uP2 = new(Program, 11, "uP2");
        public static readonly ConstantId uP3 = new(Program, 12, "uP3");
        public static readonly ConstantId uP4 = new(Program, 13, "uP4");
    }

    /// <summary>The <c>reactive.tunnel</c> program.</summary>
    public static class ReactiveTunnel
    {
        public static readonly ProgramId Program = new(20);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uT = new(Program, 1, "uT");
        public static readonly ConstantId uRot = new(Program, 2, "uRot");
        public static readonly ConstantId uDepth = new(Program, 3, "uDepth");
        public static readonly ConstantId uSym = new(Program, 4, "uSym");
        public static readonly ConstantId uLow = new(Program, 5, "uLow");
        public static readonly ConstantId uAmount = new(Program, 6, "uAmount");
        public static readonly ConstantId uBright = new(Program, 7, "uBright");
        public static readonly ConstantId uNcol = new(Program, 8, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 9, "uP0");
        public static readonly ConstantId uP1 = new(Program, 10, "uP1");
        public static readonly ConstantId uP2 = new(Program, 11, "uP2");
        public static readonly ConstantId uP3 = new(Program, 12, "uP3");
        public static readonly ConstantId uP4 = new(Program, 13, "uP4");
    }

    /// <summary>The <c>reactive.kaleidoscope</c> program.</summary>
    public static class ReactiveKaleidoscope
    {
        public static readonly ProgramId Program = new(21);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uT = new(Program, 1, "uT");
        public static readonly ConstantId uRot = new(Program, 2, "uRot");
        public static readonly ConstantId uDepth = new(Program, 3, "uDepth");
        public static readonly ConstantId uSym = new(Program, 4, "uSym");
        public static readonly ConstantId uLow = new(Program, 5, "uLow");
        public static readonly ConstantId uAmount = new(Program, 6, "uAmount");
        public static readonly ConstantId uBright = new(Program, 7, "uBright");
        public static readonly ConstantId uNcol = new(Program, 8, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 9, "uP0");
        public static readonly ConstantId uP1 = new(Program, 10, "uP1");
        public static readonly ConstantId uP2 = new(Program, 11, "uP2");
        public static readonly ConstantId uP3 = new(Program, 12, "uP3");
        public static readonly ConstantId uP4 = new(Program, 13, "uP4");
    }

    /// <summary>The <c>reactive.pulse</c> program.</summary>
    public static class ReactivePulse
    {
        public static readonly ProgramId Program = new(22);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uT = new(Program, 1, "uT");
        public static readonly ConstantId uRot = new(Program, 2, "uRot");
        public static readonly ConstantId uDepth = new(Program, 3, "uDepth");
        public static readonly ConstantId uSym = new(Program, 4, "uSym");
        public static readonly ConstantId uLow = new(Program, 5, "uLow");
        public static readonly ConstantId uAmount = new(Program, 6, "uAmount");
        public static readonly ConstantId uBright = new(Program, 7, "uBright");
        public static readonly ConstantId uNcol = new(Program, 8, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 9, "uP0");
        public static readonly ConstantId uP1 = new(Program, 10, "uP1");
        public static readonly ConstantId uP2 = new(Program, 11, "uP2");
        public static readonly ConstantId uP3 = new(Program, 12, "uP3");
        public static readonly ConstantId uP4 = new(Program, 13, "uP4");
    }

    /// <summary>The <c>reactive.vortex</c> program.</summary>
    public static class ReactiveVortex
    {
        public static readonly ProgramId Program = new(23);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uT = new(Program, 1, "uT");
        public static readonly ConstantId uRot = new(Program, 2, "uRot");
        public static readonly ConstantId uDepth = new(Program, 3, "uDepth");
        public static readonly ConstantId uSym = new(Program, 4, "uSym");
        public static readonly ConstantId uLow = new(Program, 5, "uLow");
        public static readonly ConstantId uAmount = new(Program, 6, "uAmount");
        public static readonly ConstantId uBright = new(Program, 7, "uBright");
        public static readonly ConstantId uNcol = new(Program, 8, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 9, "uP0");
        public static readonly ConstantId uP1 = new(Program, 10, "uP1");
        public static readonly ConstantId uP2 = new(Program, 11, "uP2");
        public static readonly ConstantId uP3 = new(Program, 12, "uP3");
        public static readonly ConstantId uP4 = new(Program, 13, "uP4");
    }

    /// <summary>The <c>reactive.starwarp</c> program.</summary>
    public static class ReactiveStarwarp
    {
        public static readonly ProgramId Program = new(24);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uT = new(Program, 1, "uT");
        public static readonly ConstantId uRot = new(Program, 2, "uRot");
        public static readonly ConstantId uDepth = new(Program, 3, "uDepth");
        public static readonly ConstantId uSym = new(Program, 4, "uSym");
        public static readonly ConstantId uLow = new(Program, 5, "uLow");
        public static readonly ConstantId uAmount = new(Program, 6, "uAmount");
        public static readonly ConstantId uBright = new(Program, 7, "uBright");
        public static readonly ConstantId uNcol = new(Program, 8, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 9, "uP0");
        public static readonly ConstantId uP1 = new(Program, 10, "uP1");
        public static readonly ConstantId uP2 = new(Program, 11, "uP2");
        public static readonly ConstantId uP3 = new(Program, 12, "uP3");
        public static readonly ConstantId uP4 = new(Program, 13, "uP4");
    }

    /// <summary>The <c>content.psprite</c> program.</summary>
    public static class ContentPsprite
    {
        public static readonly ProgramId Program = new(25);
        public static readonly ConstantId uSide = new(Program, 0, "uSide");
        public static readonly ConstantId uAspect = new(Program, 1, "uAspect");
        public static readonly ConstantId uTarget = new(Program, 2, "uTarget");
        public static readonly ConstantId uSize = new(Program, 3, "uSize");
        public static readonly ConstantId uSizeOverLife = new(Program, 4, "uSizeOverLife");
        public static readonly ConstantId uColorBy = new(Program, 5, "uColorBy");
        public static readonly ConstantId uSpeedRef = new(Program, 6, "uSpeedRef");
        public static readonly ConstantId uOpacity = new(Program, 7, "uOpacity");
        public static readonly ConstantId uAtlas = new(Program, 8, "uAtlas");
        public static readonly ConstantId uGrow = new(Program, 9, "uGrow");
        public static readonly ConstantId uNcol = new(Program, 10, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 11, "uP0");
        public static readonly ConstantId uP1 = new(Program, 12, "uP1");
        public static readonly ConstantId uP2 = new(Program, 13, "uP2");
        public static readonly ConstantId uP3 = new(Program, 14, "uP3");
        public static readonly ConstantId uP4 = new(Program, 15, "uP4");
        public static readonly TextureId uS0 = new(Program, 0, "uS0");
        public static readonly TextureId uS1 = new(Program, 1, "uS1");
        public static readonly TextureId uTex = new(Program, 2, "uTex");
    }

    /// <summary>The <c>content.pstep</c> program.</summary>
    public static class ContentPstep
    {
        public static readonly ProgramId Program = new(26);
        public static readonly ConstantId uSide = new(Program, 0, "uSide");
        public static readonly ConstantId uCount = new(Program, 1, "uCount");
        public static readonly ConstantId uStep = new(Program, 2, "uStep");
        public static readonly ConstantId uSeedHash = new(Program, 3, "uSeedHash");
        public static readonly ConstantId uLifeMin = new(Program, 4, "uLifeMin");
        public static readonly ConstantId uLifeMax = new(Program, 5, "uLifeMax");
        public static readonly ConstantId uRate = new(Program, 6, "uRate");
        public static readonly ConstantId uDt = new(Program, 7, "uDt");
        public static readonly ConstantId uAspect = new(Program, 8, "uAspect");
        public static readonly ConstantId uEmitter = new(Program, 9, "uEmitter");
        public static readonly ConstantId uEmit = new(Program, 10, "uEmit");
        public static readonly ConstantId uBirth = new(Program, 11, "uBirth");
        public static readonly ConstantId uThreshold = new(Program, 12, "uThreshold");
        public static readonly ConstantId uHasPicture = new(Program, 13, "uHasPicture");
        public static readonly ConstantId uForces = new(Program, 14, "uForces");
        public static readonly ConstantId uCurl = new(Program, 15, "uCurl");
        public static readonly ConstantId uVortex = new(Program, 16, "uVortex");
        public static readonly ConstantId uAttract = new(Program, 17, "uAttract");
        public static readonly ConstantId uSurge = new(Program, 18, "uSurge");
        public static readonly ConstantId uEdges = new(Program, 19, "uEdges");
        public static readonly ConstantId uRestitution = new(Program, 20, "uRestitution");
        public static readonly ConstantId uWeight = new(Program, 21, "uWeight");
        public static readonly ConstantId uInvert = new(Program, 22, "uInvert");
        public static readonly ConstantId uFlow = new(Program, 23, "uFlow");
        public static readonly ConstantId uColorSource = new(Program, 24, "uColorSource");
        public static readonly TextureId uS0 = new(Program, 0, "uS0");
        public static readonly TextureId uS1 = new(Program, 1, "uS1");
        public static readonly TextureId uPicture = new(Program, 2, "uPicture");
    }

    /// <summary>The <c>content.raymarch</c> program.</summary>
    public static class ContentRaymarch
    {
        public static readonly ProgramId Program = new(27);
        public static readonly ConstantId uRes = new(Program, 0, "uRes");
        public static readonly ConstantId uEye = new(Program, 1, "uEye");
        public static readonly ConstantId uRight = new(Program, 2, "uRight");
        public static readonly ConstantId uUp = new(Program, 3, "uUp");
        public static readonly ConstantId uFwd = new(Program, 4, "uFwd");
        public static readonly ConstantId uTanHalf = new(Program, 5, "uTanHalf");
        public static readonly ConstantId uKind = new(Program, 6, "uKind");
        public static readonly ConstantId uIters = new(Program, 7, "uIters");
        public static readonly ConstantId uPower = new(Program, 8, "uPower");
        public static readonly ConstantId uScale = new(Program, 9, "uScale");
        public static readonly ConstantId uFoldLimit = new(Program, 10, "uFoldLimit");
        public static readonly ConstantId uMinR = new(Program, 11, "uMinR");
        public static readonly ConstantId uFixedR = new(Program, 12, "uFixedR");
        public static readonly ConstantId uFold = new(Program, 13, "uFold");
        public static readonly ConstantId uOffset = new(Program, 14, "uOffset");
        public static readonly ConstantId uTwist = new(Program, 15, "uTwist");
        public static readonly ConstantId uSteps = new(Program, 16, "uSteps");
        public static readonly ConstantId uCone = new(Program, 17, "uCone");
        public static readonly ConstantId uGlow = new(Program, 18, "uGlow");
        public static readonly ConstantId uAo = new(Program, 19, "uAo");
        public static readonly ConstantId uFog = new(Program, 20, "uFog");
        public static readonly ConstantId uBright = new(Program, 21, "uBright");
        public static readonly ConstantId uPhase = new(Program, 22, "uPhase");
        public static readonly ConstantId uBg = new(Program, 23, "uBg");
        public static readonly ConstantId uMode = new(Program, 24, "uMode");
        public static readonly ConstantId uNcol = new(Program, 25, "uNcol");
        public static readonly ConstantId uP0 = new(Program, 26, "uP0");
        public static readonly ConstantId uP1 = new(Program, 27, "uP1");
        public static readonly ConstantId uP2 = new(Program, 28, "uP2");
        public static readonly ConstantId uP3 = new(Program, 29, "uP3");
        public static readonly ConstantId uP4 = new(Program, 30, "uP4");
    }

    /// <summary>The <c>fxmix</c> program.</summary>
    public static class Fxmix
    {
        public static readonly ProgramId Program = new(28);
        public static readonly ConstantId uFxBottomUp = new(Program, 0, "uFxBottomUp");
        public static readonly ConstantId uBlend = new(Program, 1, "uBlend");
        public static readonly ConstantId uMix = new(Program, 2, "uMix");
        public static readonly TextureId uInput = new(Program, 0, "uInput");
        public static readonly TextureId uFx = new(Program, 1, "uFx");
    }

    /// <summary>The <c>hap</c> program.</summary>
    public static class Hap
    {
        public static readonly ProgramId Program = new(29);
        public static readonly ConstantId uMode = new(Program, 0, "uMode");
        public static readonly ConstantId uHdr = new(Program, 1, "uHdr");
        public static readonly TextureId uTex0 = new(Program, 0, "uTex0");
        public static readonly TextureId uTex1 = new(Program, 1, "uTex1");
    }

    /// <summary>The <c>hapenc.bc1</c> program.</summary>
    public static class HapencBc1
    {
        public static readonly ProgramId Program = new(30);
        public static readonly ConstantId uSize = new(Program, 0, "uSize");
        public static readonly ConstantId uRefine = new(Program, 1, "uRefine");
        public static readonly TextureId uSrc = new(Program, 0, "uSrc");
    }

    /// <summary>The <c>hapenc.bc3</c> program.</summary>
    public static class HapencBc3
    {
        public static readonly ProgramId Program = new(31);
        public static readonly ConstantId uSize = new(Program, 0, "uSize");
        public static readonly ConstantId uRefine = new(Program, 1, "uRefine");
        public static readonly TextureId uSrc = new(Program, 0, "uSrc");
    }

    /// <summary>The <c>hapenc.bc4</c> program.</summary>
    public static class HapencBc4
    {
        public static readonly ProgramId Program = new(32);
        public static readonly ConstantId uSize = new(Program, 0, "uSize");
        public static readonly ConstantId uRefine = new(Program, 1, "uRefine");
        public static readonly TextureId uSrc = new(Program, 0, "uSrc");
    }

    /// <summary>The <c>hapenc.ycocg</c> program.</summary>
    public static class HapencYcocg
    {
        public static readonly ProgramId Program = new(33);
        public static readonly ConstantId uSize = new(Program, 0, "uSize");
        public static readonly ConstantId uRefine = new(Program, 1, "uRefine");
        public static readonly TextureId uSrc = new(Program, 0, "uSrc");
    }

    /// <summary>The <c>led_patch</c> program.</summary>
    public static class LedPatch
    {
        public static readonly ProgramId Program = new(34);
        public static readonly ConstantId uLevel = new(Program, 0, "uLevel");
        public static readonly ConstantId uTest = new(Program, 1, "uTest");
        public static readonly ConstantId uStep = new(Program, 2, "uStep");
        public static readonly ConstantId uFixtures = new(Program, 3, "uFixtures");
        public static readonly TextureId uColors = new(Program, 0, "uColors");
        public static readonly TextureId uPatch = new(Program, 1, "uPatch");
        public static readonly TextureId uFix = new(Program, 2, "uFix");
        public static readonly TextureId uAir = new(Program, 3, "uAir");
    }

    /// <summary>The <c>led_sample</c> program.</summary>
    public static class LedSample
    {
        public static readonly ProgramId Program = new(35);
        public static readonly ConstantId uFirst = new(Program, 0, "uFirst");
        public static readonly ConstantId uCount = new(Program, 1, "uCount");
        public static readonly ConstantId uTaps = new(Program, 2, "uTaps");
        public static readonly ConstantId uGamma = new(Program, 3, "uGamma");
        public static readonly ConstantId uSpread = new(Program, 4, "uSpread");
        public static readonly ConstantId uTexSize = new(Program, 5, "uTexSize");
        public static readonly TextureId uAttr = new(Program, 0, "uAttr");
        public static readonly TextureId uTex = new(Program, 1, "uTex");
    }

    /// <summary>The <c>overlay.shapes</c> program.</summary>
    public static class OverlayShapes
    {
        public static readonly ProgramId Program = new(36);
        public static readonly ConstantId uTarget = new(Program, 0, "uTarget");
    }

    /// <summary>The <c>pixelsurface</c> program.</summary>
    public static class Pixelsurface
    {
        public static readonly ProgramId Program = new(37);
        public static readonly ConstantId uTarget = new(Program, 0, "uTarget");
        public static readonly ConstantId uRegion = new(Program, 1, "uRegion");
        public static readonly ConstantId uHasMask = new(Program, 2, "uHasMask");
        public static readonly ConstantId uOpacity = new(Program, 3, "uOpacity");
        public static readonly TextureId uSource = new(Program, 0, "uSource");
        public static readonly TextureId uMask = new(Program, 1, "uMask");
    }

    /// <summary>The <c>video.pack</c> program.</summary>
    public static class VideoPack
    {
        public static readonly ProgramId Program = new(38);
        public static readonly ConstantId uMode = new(Program, 0, "uMode");
        public static readonly ConstantId uWidth = new(Program, 1, "uWidth");
        public static readonly ConstantId uHeight = new(Program, 2, "uHeight");
        public static readonly ConstantId uGain = new(Program, 3, "uGain");
        public static readonly TextureId uPicture = new(Program, 0, "uPicture");
    }

    /// <summary>The <c>video.unpack</c> program.</summary>
    public static class VideoUnpack
    {
        public static readonly ProgramId Program = new(39);
        public static readonly ConstantId uMode = new(Program, 0, "uMode");
        public static readonly ConstantId uHasAlpha = new(Program, 1, "uHasAlpha");
        public static readonly ConstantId uPairs = new(Program, 2, "uPairs");
        public static readonly ConstantId uCode = new(Program, 3, "uCode");
        public static readonly ConstantId uMatrix = new(Program, 4, "uMatrix");
        public static readonly TextureId uLuma = new(Program, 0, "uLuma");
        public static readonly TextureId uChroma = new(Program, 1, "uChroma");
        public static readonly TextureId uAlpha = new(Program, 2, "uAlpha");
    }
}
