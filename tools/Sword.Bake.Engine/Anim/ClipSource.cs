namespace Sword.Bake.Engine.Anim;

/// <summary>Сводка переноса пальцев: суммы по перенесённым клипам, градусы.</summary>
public sealed class FingerStats
{
    public int Clips;
    public double DevBefore, DevAfter, MetaBefore, MetaAfter;
}

/// <summary>
/// Клипы raw-экспорта для импорта клипов и крепления оружия: чтение, перенос пальцев (если задан источник) и кэш.
/// Крепление меча и сборка клипов берут одни и те же кадры, перенос выполняется по разу на клип.
/// </summary>
public sealed class ClipSource(RawManifest manifest, string dir, FingerSource? fingers = null)
{
    public readonly RawManifest Manifest = manifest;
    public readonly string Dir = dir;
    public readonly FingerSource? Fingers = fingers;
    public readonly UeSpace Space = new(manifest);
    /// <summary>Сводка переноса по загруженным клипам; null — пальцы не переносятся.</summary>
    public readonly FingerStats? Stats = fingers is not null ? new FingerStats() : null;
    private readonly Dictionary<RawClipInfo, RawClip> _cache = [];

    public RawClip Load(RawClipInfo info)
    {
        if (_cache.TryGetValue(info, out var cached)) return cached;
        var raw = RawAnimIO.ReadClip(Dir, Manifest, info);
        if (Fingers?.Source(info) is { } src)
        {
            var stats = Stats!;
            stats.DevBefore += FingerTransfer.Deviation(Manifest, raw, Fingers.Manifest, src);
            stats.MetaBefore += FingerTransfer.MetacarpalBend(Manifest, raw);
            FingerTransfer.Apply(Manifest, raw, Fingers.Manifest, src, Fingers.Rest);
            stats.DevAfter += FingerTransfer.Deviation(Manifest, raw, Fingers.Manifest, src);
            stats.MetaAfter += FingerTransfer.MetacarpalBend(Manifest, raw);
            stats.Clips++;
        }
        _cache[info] = raw;
        return raw;
    }
}
