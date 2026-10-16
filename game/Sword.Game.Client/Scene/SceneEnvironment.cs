using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Render;

namespace Sword.Game.Client.Scene;

/// <summary>
/// Окружение сцены арены — цвета освещения <c>Shade</c> (lighting.hlsli) и фон, линейные HDR до экспозиции. Лежит в
/// константах кадра (<see cref="FrameConstants.Environment"/>, строки по 16 байт, <c>.w</c> — выравнивание). Только представление.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SceneEnvironment
{
    public Vector3 Sun; public float Pad0;
    public Vector3 SkyLow; public float Pad1;
    public Vector3 SkyHigh; public float Pad2;
    public Vector3 Rim; public float Pad3;
    public Vector3 Fog; public float Pad4;

    /// <summary>Туман <c>Shade</c> (lighting.hlsli): доля saturate((d − начало) / длина) по расстоянию до камеры, м — те же числа, что в шейдере.</summary>
    public const float FogStart = 25f, FogRange = 140f;

    /// <summary>
    /// Фон — где за кадр не легло ничего. Равен цвету очистки HDR-сцены (<see cref="ArenaClient.ClearColour"/>) — проход
    /// фона не нужен; иначе клиент рисует его поверх: цель сцены при смене окружения не пересоздаётся.
    /// </summary>
    public Vector3 Background; public float Pad5;

    public SceneEnvironment(Vector3 sun, Vector3 skyLow, Vector3 skyHigh, Vector3 rim, Vector3 fog, Vector3 background)
    {
        Sun = sun; SkyLow = skyLow; SkyHigh = skyHigh; Rim = rim; Fog = fog; Background = background;
        Pad0 = Pad1 = Pad2 = Pad3 = Pad4 = Pad5 = 0;
    }

    /// <summary>День — прежние константы освещения арены (кадр бит в бит тот же), фон — цвет очистки.</summary>
    public static readonly SceneEnvironment Day = new(
        sun: new(1.25f, 1.1f, 0.95f),
        skyLow: new(0.16f, 0.12f, 0.1f), skyHigh: new(0.32f, 0.36f, 0.45f),
        rim: new(0.9f, 0.7f, 0.5f),
        fog: new(0.10f, 0.085f, 0.08f),
        background: new(ArenaClient.ClearColour.X, ArenaClient.ClearColour.Y, ArenaClient.ClearColour.Z));

    /// <summary>Цвет фона ночи на экране (sRGB): почти чёрный с синевой, как ночной кадр LIVE-2.</summary>
    public static readonly Vector3 NightBackgroundDisplay = new(1 / 255f, 0, 2 / 255f);

    /// <summary>
    /// Ночь: слабая холодная луна с направления солнца, тёмное небо, холодный контур (силуэты различимы); туман и фон —
    /// один почти чёрный цвет, без шва. Главный источник — точечный свет (факел).
    /// </summary>
    public static readonly SceneEnvironment Night = new(
        sun: new(0.035f, 0.045f, 0.075f),
        skyLow: new(0.004f, 0.004f, 0.006f), skyHigh: new(0.012f, 0.016f, 0.03f),
        rim: new(0.05f, 0.07f, 0.12f),
        fog: FilmicTonemap.SceneFor(NightBackgroundDisplay, Renderer.DefaultExposure),
        background: FilmicTonemap.SceneFor(NightBackgroundDisplay, Renderer.DefaultExposure));
}
