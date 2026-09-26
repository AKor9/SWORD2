// Корневые константы прохода (b2) и статические сэмплеры приложения (PipelineCache).
// Двадцать восемь скаляров — ровно столько корневых констант (RootLayout.ConstantCount).
#ifndef SWORD_PASS_HLSLI
#define SWORD_PASS_HLSLI

cbuffer PassConstants : register(b2)
{
    uint gPass0;  uint gPass1;  uint gPass2;  uint gPass3;
    uint gPass4;  uint gPass5;  uint gPass6;  uint gPass7;
    uint gPass8;  uint gPass9;  uint gPass10; uint gPass11;
    uint gPass12; uint gPass13; uint gPass14; uint gPass15;
    uint gPass16; uint gPass17; uint gPass18; uint gPass19;
    uint gPass20; uint gPass21; uint gPass22; uint gPass23;
    uint gPass24; uint gPass25; uint gPass26; uint gPass27;
};

#if PASS_COUNT != 28
#error pass.hlsli declares 28 gPassN, RootLayout.ConstantCount says otherwise
#endif

SamplerState gLinearClamp : register(s0);
SamplerState gLinearWrap  : register(s1);
SamplerState gAnisoClamp  : register(s2);
SamplerState gLinearBorder : register(s3);   // за краем - прозрачный чёрный (гауссианы bloom: свет из-за края не приходит)

#endif
