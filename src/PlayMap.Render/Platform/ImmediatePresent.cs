using PlayMap.Core.Gpu;
using PlayMap.Core.Presentation;

namespace PlayMap.Render.Platform;

/// <summary>A presenter that also shows a picture in one call — the picture drawn into its next buffer, then presented — as the frame
/// code calls it while it draws through the GL device. Every presenter in this assembly is one: its <see cref="IOutputPresenter.Record"/>
/// takes the picture its <see cref="IOutputPresenter.Present"/> draws and shows, the GL window's context being its own.</summary>
public interface IImmediatePresenter : IOutputPresenter
{
    /// <summary>Shows a finished picture.</summary>
    PresentResult Present(GpuTexture picture);

    /// <summary>Shows black (an output with nothing drawn yet).</summary>
    PresentResult PresentBlack();
}

/// <summary>The one-call present on any presenter: an immediate one shows the picture; one that only records into a frame's list is
/// refused.</summary>
public static class ImmediatePresenting
{
    extension(IOutputPresenter presenter)
    {
        /// <summary>Shows a finished picture in one call.</summary>
        public PresentResult Present(GpuTexture picture) => Immediate(presenter).Present(picture);

        /// <summary>Shows black in one call.</summary>
        public PresentResult PresentBlack() => Immediate(presenter).PresentBlack();
    }

    private static IImmediatePresenter Immediate(IOutputPresenter presenter) =>
        presenter as IImmediatePresenter
        ?? throw new NotSupportedException($"a {presenter.Path} presenter records into the frame's list and presents after its submit");
}
