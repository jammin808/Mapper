namespace PlayMap.Core.Content;

/// <summary>The GPU particle field's birth schedule, in integers only so every GPU and this C# twin agree exactly. Slot i lives
/// <c>life_i</c> steps (between the minimum and maximum life) and is born whenever <c>(n − phase_i) mod life_i == 0</c>; its k-th
/// life draws everything (alive or dormant, where, how fast) from hashes of (i, k). A slot's state therefore depends only on the
/// steps since its last birth: a field run from any step at least one longest life back is the same field (bounded memory).
/// Steps are counted on the generator's time grid with a bias, so a step a lifetime before the origin is still a positive
/// integer in the shader.</summary>
public static class ParticleSchedule
{
    /// <summary>Steps a second.</summary>
    public const int StepsPerSecond = 120;

    /// <summary>Added to every step index the shader sees: 2³⁰ steps (about 104 days) before the origin are still positive, past any
    /// AT a RETIME takes; the biased index wraps 2³² − 2³⁰ steps (about 310 days) after the origin.</summary>
    public const long StepBias = 1L << 30;

    public const uint KeyLife = 0x9E3779B9u;
    public const uint KeyPhase = 0x85EBCA6Bu;
    public const uint KeyAlive = 0xC2B2AE35u;
    public const uint KeyLifeIndex = 0x27D4EB2Fu;

    /// <summary>PCG's output hash of one 32-bit word (the shader's <c>pcg</c>, word for word).</summary>
    public static uint Pcg(uint v)
    {
        unchecked
        {
            var state = v * 747796405u + 2891336453u;
            var word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
            return (word >> 22) ^ word;
        }
    }

    /// <summary>A hash as a float in [0, 1): its top 24 bits, exact in single precision (the shader's <c>u01</c>).</summary>
    public static float U01(uint h) => (h >> 8) * (1f / 16777216f);

    /// <summary>The step index the shader sees for a generator step (biased, wrapped to 32 bits).</summary>
    public static uint Biased(long step) => unchecked((uint)(step + StepBias));

    /// <summary>Slot <paramref name="index"/>'s life in steps and its phase within it.</summary>
    public static (int Life, int Phase) Slot(uint index, int lifeMin, int lifeMax)
    {
        lifeMin = Math.Max(1, lifeMin);
        lifeMax = Math.Max(lifeMin, lifeMax);
        var span = (uint)(lifeMax - lifeMin + 1);
        var life = lifeMin + (int)(Pcg(index ^ KeyLife) % span);
        var phase = (int)(Pcg(index ^ KeyPhase) % (uint)life);
        return (life, phase);
    }

    /// <summary>At a biased step: whether slot <paramref name="index"/> is born on it, which of its lives it is in, and how many
    /// steps that life has run (0 on the birth step).</summary>
    public static (bool Birth, uint LifeIndex, int Since) At(uint index, uint biasedStep, int lifeMin, int lifeMax)
    {
        var (life, phase) = Slot(index, lifeMin, lifeMax);
        var shifted = biasedStep - (uint)phase;
        var since = (int)(shifted % (uint)life);
        var k = (shifted - (uint)since) / (uint)life;
        return (since == 0, k, since);
    }

    /// <summary>Whether slot <paramref name="index"/>'s life <paramref name="lifeIndex"/> is lit at a birth rate (0–1 of the slots).</summary>
    public static bool Alive(uint index, uint lifeIndex, double rate) =>
        U01(Pcg(unchecked(index * KeyAlive) ^ Pcg(lifeIndex ^ KeyLifeIndex))) < (float)Math.Clamp(rate, 0, 1);
}
