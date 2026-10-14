using PlayMap.Core.Content;
using PlayMap.Core.Modulation;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.Audio;

/// <summary>An effect kind's numeric parameters as the effects catalogue of the live show declares them (built-ins and the show's
/// ISF files): a slot's own numbers bind with their range and default, a flag, a choice, a colour or a point does not.</summary>
internal sealed class EffectRanges(EngineCore engine) : IFxParamRanges
{
    public bool TryRange(string kind, string param, out double min, out double max, out double defaultValue)
    {
        min = max = defaultValue = 0;
        var spec = FxCatalog.For(engine.Store.Project.FxAssets).Find(kind)?.Param(param);
        if (spec is not { Type: ParamType.Number } || !(spec.Max > spec.Min))
            return false;
        min = spec.Min;
        max = spec.Max;
        defaultValue = double.TryParse(spec.Default, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : min;
        return true;
    }
}
