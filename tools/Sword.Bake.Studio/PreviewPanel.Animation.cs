using System.Globalization;
using Sword.Engine.Ui;

namespace Sword.Bake.Studio;

/// <summary>
/// Анимация сцены (<see cref="PreviewScene.Animation"/>): слева — клипы с фильтром, справа — 3D-сцена в позе клипа,
/// под ней — пуск и пауза, шкала времени и скорость. Пока клип играет, окно перерисовывается каждый кадр.
/// ↑/↓ — предыдущий/следующий клип, пробел — пуск/пауза, ←/→ — кадр назад/вперёд (на паузе). Клипов нет (скелет) —
/// неподвижная сцена обработчика.
/// </summary>
internal sealed partial class PreviewPanel
{
    private static readonly string[] Speeds = ["×0.1", "×0.25", "×0.5", "×1", "×2"];
    private static readonly float[] SpeedValues = [0.1f, 0.25f, 0.5f, 1, 2];

    private int _clip;
    private float _clipTime;
    private bool _playing = !still;
    private int _speed = 3;
    private double _lastTick = double.NaN;
    private string _clipFilter = "";
    private string? _clipFilterSeen;
    private readonly List<int> _clipRows = [];
    private int _scrollClipTo = -1;
    private readonly List<string> _animStatus = [];
    /// <summary>Поза, стоящая в сцене: клип и время.</summary>
    private (int Clip, float Time) _posed = (-1, float.NaN);

    private void PoseAnimation(PreviewAnimation anim, bool force = false)
    {
        if (anim.Clips.Count == 0) return;
        _clip = Math.Clamp(_clip, 0, anim.Clips.Count - 1);
        if (!force && _posed == (_clip, _clipTime)) return;
        _animStatus.Clear();
        anim.Pose(_clip, _clipTime, _scene!, _animStatus);
        _posed = (_clip, _clipTime);
    }

    private void DrawAnimation(UiContext ui, UiRect r)
    {
        var anim = _scene!.Animation!;
        var clips = anim.Clips;
        if (clips.Count == 0)
        {
            Draw3D(ui, r);
            return;
        }

        var list = r.SplitLeft(ui.S(280), out r);
        r.SplitLeft(ui.S(8), out r);
        DrawClipList(ui, list, clips);

        var clip = clips[_clip];
        float duration = MathF.Max(clip.Duration, 0);
        float frameStep = clip.FrameRate > 0 ? 1 / clip.FrameRate : 1 / 30f;

        // клавиши — пока поле фильтра без фокуса
        if (!ui.WantsKeyboard) HandleClipKeys(ui, duration, frameStep);

        // время
        double now = ui.Input.Time;
        if (_playing && duration > 0)
        {
            float dt = double.IsNaN(_lastTick) ? 0 : (float)Math.Clamp(now - _lastTick, 0, 0.1);
            _clipTime += dt * SpeedValues[_speed];
            if (_clipTime > duration) _clipTime %= duration;
            ui.RedrawAfter(0);
        }
        _lastTick = now;

        var bar = r.SplitBottom(ui.S(30), out var view);
        view.SplitBottom(ui.S(6), out view);
        DrawPlayer(ui, bar.Shrink(0, ui.S(2)), clip, duration);

        PoseAnimation(anim);
        Draw3D(ui, view);
    }

    /// <summary>Список клипов с фильтром и числом.</summary>
    private void DrawClipList(UiContext ui, UiRect list, IReadOnlyList<PreviewClip> clips)
    {
        var t = ui.Theme;
        ui.TextField(list.SplitTop(ui.S(28), out list), "preview-clip-filter", ref _clipFilter, "фильтр клипов");
        list.SplitTop(ui.S(4), out list);
        if (_clipFilter != _clipFilterSeen)
        {
            _clipFilterSeen = _clipFilter;
            _clipRows.Clear();
            for (int i = 0; i < clips.Count; i++)
                if (_clipFilter.Length == 0 || clips[i].Name.Contains(_clipFilter, StringComparison.OrdinalIgnoreCase))
                    _clipRows.Add(i);
        }
        var count = list.SplitBottom(ui.S(20), out list);
        ui.Label(count, _clipRows.Count == clips.Count ? $"клипов {clips.Count}" : $"клипов {_clipRows.Count} из {clips.Count}",
            t.TextFaint, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        float rowHeight = ui.S(t.RowHeight);
        if (_scrollClipTo >= 0)
        {
            ui.ScrollIntoView("preview-clips", _scrollClipTo * rowHeight, (_scrollClipTo + 1) * rowHeight, list.Height);
            _scrollClipTo = -1;
        }
        ui.VirtualList(list, "preview-clips", _clipRows.Count, rowHeight, (row, rr) =>
        {
            int i = _clipRows[row];
            if (ui.Selectable(rr, ui.Id(i + 1), i == _clip) && i != _clip)
            {
                _clip = i;
                _clipTime = 0;
                _playing = true;
            }
            var c = clips[i];
            var text = rr.Shrink(ui.S(6), 0);
            var dur = text.SplitRight(ui.S(48), out text);
            ui.Draw.PushClip(text);
            ui.Label(text, c.Name, i == _clip ? t.Text : t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
            ui.Draw.PopClip();
            ui.Label(dur, string.Create(CultureInfo.InvariantCulture, $"{c.Duration:0.00} с"), t.TextFaint,
                UiAlign.Right, UiFace.Body, t.SmallFontSize);
        });
    }

    private void HandleClipKeys(UiContext ui, float duration, float frameStep)
    {
        int step = ui.Input.TakeKey(UiKey.Down) ? 1 : ui.Input.TakeKey(UiKey.Up) ? -1 : 0;
        if (step != 0 && _clipRows.Count > 0)
        {
            int row = _clipRows.IndexOf(_clip);
            row = Math.Clamp(row < 0 ? (step > 0 ? 0 : _clipRows.Count - 1) : row + step, 0, _clipRows.Count - 1);
            if (_clip != _clipRows[row])
            {
                _clip = _clipRows[row];
                _clipTime = 0;
                _playing = true;
            }
            _scrollClipTo = row;
        }
        if (ui.Input.TakeKey(UiKey.Space)) _playing = !_playing;
        if (ui.Input.TakeKey(UiKey.Left)) { _playing = false; _clipTime = MathF.Max(0, _clipTime - frameStep); }
        if (ui.Input.TakeKey(UiKey.Right)) { _playing = false; _clipTime = MathF.Min(duration, _clipTime + frameStep); }
    }

    /// <summary>Проигрыватель: пуск и пауза, скорость, время и кадр, шкала.</summary>
    private void DrawPlayer(UiContext ui, UiRect bar, PreviewClip clip, float duration)
    {
        var t = ui.Theme;
        if (ui.Button(bar.SplitLeft(ui.S(76), out bar), _playing ? "Пауза" : "Пуск", id: "preview-play")) _playing = !_playing;
        bar.SplitLeft(ui.S(8), out bar);
        int speed = _speed;
        if (ui.Combo(bar.SplitRight(ui.S(80), out bar), "preview-speed", ref speed, Speeds)) _speed = speed;
        bar.SplitRight(ui.S(8), out bar);
        int frames = (int)MathF.Round(duration * clip.FrameRate) + 1;
        int frame = (int)MathF.Round(_clipTime * clip.FrameRate);
        var label = bar.SplitRight(ui.S(190), out bar);
        ui.Label(label, string.Create(CultureInfo.InvariantCulture, $"{_clipTime:0.00} / {duration:0.00} с · кадр {frame + 1}/{frames}"),
            t.TextDim, UiAlign.Right, UiFace.Body, t.SmallFontSize);
        bar.SplitRight(ui.S(10), out bar);
        float time = _clipTime;
        if (ui.Slider(bar, "preview-time", ref time, 0, MathF.Max(duration, 1e-3f)))
        {
            _clipTime = time;
            _playing = false;
        }
    }
}
