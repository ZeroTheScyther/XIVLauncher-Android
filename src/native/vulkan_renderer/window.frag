#version 450
layout(binding = 0) uniform sampler2D texSampler;
layout(push_constant) uniform PC {
    float ndcX0;
    float ndcY0;
    float ndcX1;
    float ndcY1;
    int   useTexAlpha;
    int   effectId;
    float sharpness;
    float resW;
    float resH;
    int   effectMask;
    float brightness;
    float contrast;
    float gamma;
    float outW;
    float outH;
} pc;

layout(location = 0) in  vec2 fragTexCoord;
layout(location = 0) out vec4 outColor;

const int EFFECT_MASK_TOON  = 1;
const int EFFECT_MASK_FXAA  = 2;
const int EFFECT_MASK_VIVID = 4;
const int EFFECT_MASK_CRT   = 8;
const int EFFECT_MASK_NTSC  = 16;

bool hasEffect(int mask) {
    return (pc.effectMask & mask) != 0;
}

// ---- AMD FidelityFX Super Resolution 1.0 EASU (edge-adaptive spatial upscale) ----
// Ported from the official GPUOpen source (ffx_fsr1.h), MIT (c) 2021 AMD,
// to match the GLRenderer's FSR1EasuEffect. Single-pass: runs at output
// resolution, samples the input content texture in normalized UV space.
vec3 FsrEasuCF(vec2 p) { return texture(texSampler, p).rgb; }

void FsrEasuCon(out vec4 con0, out vec4 con1, out vec4 con2, out vec4 con3,
                vec2 inputViewportInPixels, vec2 inputSizeInPixels, vec2 outputSizeInPixels) {
    con0 = vec4(
        inputViewportInPixels.x / outputSizeInPixels.x,
        inputViewportInPixels.y / outputSizeInPixels.y,
        0.5 * inputViewportInPixels.x / outputSizeInPixels.x - 0.5,
        0.5 * inputViewportInPixels.y / outputSizeInPixels.y - 0.5);
    con1 = vec4( 1.0 / inputSizeInPixels.x,  1.0 / inputSizeInPixels.y,
                 1.0 / inputSizeInPixels.x, -1.0 / inputSizeInPixels.y);
    con2 = vec4(-1.0 / inputSizeInPixels.x,  2.0 / inputSizeInPixels.y,
                 1.0 / inputSizeInPixels.x,  2.0 / inputSizeInPixels.y);
    con3 = vec4( 0.0, 4.0 / inputSizeInPixels.y, 0.0, 0.0);
}

void FsrEasuTapF(inout vec3 aC, inout float aW, vec2 off, vec2 dir, vec2 len,
                 float lob, float clp, vec3 c) {
    vec2 v = vec2(off.x * dir.x + off.y * dir.y, off.x * (-dir.y) + off.y * dir.x);
    v *= len;
    float d2 = min(dot(v, v), clp);
    float wB = 0.4 * d2 - 1.0;
    float wA = lob * d2 - 1.0;
    wB *= wB;
    wA *= wA;
    wB = 1.5625 * wB - 0.5625;
    float w = wB * wA;
    aC += c * w;
    aW += w;
}

void FsrEasuSetF(inout vec2 dir, inout float len, float w,
                 float lA, float lB, float lC, float lD, float lE) {
    float dc = lD - lC;
    float cb = lC - lB;
    float lenX = max(abs(dc), abs(cb));
    lenX = 1.0 / max(lenX, 1e-6);
    float dirX = lD - lB;
    dir.x += dirX * w;
    lenX = clamp(abs(dirX) * lenX, 0.0, 1.0);
    lenX *= lenX;
    len += lenX * w;
    float ec = lE - lC;
    float ca = lC - lA;
    float lenY = max(abs(ec), abs(ca));
    lenY = 1.0 / max(lenY, 1e-6);
    float dirY = lE - lA;
    dir.y += dirY * w;
    lenY = clamp(abs(dirY) * lenY, 0.0, 1.0);
    lenY *= lenY;
    len += lenY * w;
}

void FsrEasuF(out vec3 pix, vec2 ip, vec4 con0, vec4 con1, vec4 con2, vec4 con3) {
    vec2 pp = ip * con0.xy + con0.zw;
    vec2 fp = floor(pp);
    pp -= fp;
    vec2 p0 = fp * con1.xy + con1.zw;
    vec2 p1 = p0 + con2.xy;
    vec2 p2 = p0 + con2.zw;
    vec2 p3 = p0 + con3.xy;
    vec4 off = vec4(-0.5, 0.5, -0.5, 0.5) * con1.xxyy;
    vec3 bC = FsrEasuCF(p0 + off.xw); float bL = bC.b * 0.5 + (bC.r * 0.5 + bC.g);
    vec3 cC = FsrEasuCF(p0 + off.yw); float cL = cC.b * 0.5 + (cC.r * 0.5 + cC.g);
    vec3 iC = FsrEasuCF(p1 + off.xw); float iL = iC.b * 0.5 + (iC.r * 0.5 + iC.g);
    vec3 jC = FsrEasuCF(p1 + off.yw); float jL = jC.b * 0.5 + (jC.r * 0.5 + jC.g);
    vec3 fC = FsrEasuCF(p1 + off.yz); float fL = fC.b * 0.5 + (fC.r * 0.5 + fC.g);
    vec3 eC = FsrEasuCF(p1 + off.xz); float eL = eC.b * 0.5 + (eC.r * 0.5 + eC.g);
    vec3 kC = FsrEasuCF(p2 + off.xw); float kL = kC.b * 0.5 + (kC.r * 0.5 + kC.g);
    vec3 lC = FsrEasuCF(p2 + off.yw); float lL = lC.b * 0.5 + (lC.r * 0.5 + lC.g);
    vec3 hC = FsrEasuCF(p2 + off.yz); float hL = hC.b * 0.5 + (hC.r * 0.5 + hC.g);
    vec3 gC = FsrEasuCF(p2 + off.xz); float gL = gC.b * 0.5 + (gC.r * 0.5 + gC.g);
    vec3 oC = FsrEasuCF(p3 + off.yz); float oL = oC.b * 0.5 + (oC.r * 0.5 + oC.g);
    vec3 nC = FsrEasuCF(p3 + off.xz); float nL = nC.b * 0.5 + (nC.r * 0.5 + nC.g);
    vec2 dir = vec2(0.0);
    float len = 0.0;
    FsrEasuSetF(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
    FsrEasuSetF(dir, len, pp.x * (1.0 - pp.y), cL, fL, gL, hL, kL);
    FsrEasuSetF(dir, len, (1.0 - pp.x) * pp.y, fL, iL, jL, kL, nL);
    FsrEasuSetF(dir, len, pp.x * pp.y, gL, jL, kL, lL, oL);
    float dirR = dir.x * dir.x + dir.y * dir.y;
    bool zro = dirR < (1.0 / 32768.0);
    dirR = inversesqrt(max(dirR, 1e-6));
    if (zro) { dir = vec2(1.0, 0.0); dirR = 1.0; }
    dir *= dirR;
    len = 0.5 * len;
    len *= len;
    float stretch = (dir.x * dir.x + dir.y * dir.y) / max(max(abs(dir.x), abs(dir.y)), 1e-6);
    vec2 len2 = vec2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
    float lob = 0.5 + ((1.0 / 4.0 - 0.04) - 0.5) * len;
    float clp = 1.0 / max(lob, 1e-6);
    vec3 min4 = min(min(fC, gC), min(jC, kC));
    vec3 max4 = max(max(fC, gC), max(jC, kC));
    vec3 aC = vec3(0.0);
    float aW = 0.0;
    FsrEasuTapF(aC, aW, vec2( 0.0, -1.0) - pp, dir, len2, lob, clp, bC);
    FsrEasuTapF(aC, aW, vec2( 1.0, -1.0) - pp, dir, len2, lob, clp, cC);
    FsrEasuTapF(aC, aW, vec2(-1.0,  1.0) - pp, dir, len2, lob, clp, iC);
    FsrEasuTapF(aC, aW, vec2( 0.0,  1.0) - pp, dir, len2, lob, clp, jC);
    FsrEasuTapF(aC, aW, vec2( 0.0,  0.0) - pp, dir, len2, lob, clp, fC);
    FsrEasuTapF(aC, aW, vec2(-1.0,  0.0) - pp, dir, len2, lob, clp, eC);
    FsrEasuTapF(aC, aW, vec2( 1.0,  1.0) - pp, dir, len2, lob, clp, kC);
    FsrEasuTapF(aC, aW, vec2( 2.0,  1.0) - pp, dir, len2, lob, clp, lC);
    FsrEasuTapF(aC, aW, vec2( 2.0,  0.0) - pp, dir, len2, lob, clp, hC);
    FsrEasuTapF(aC, aW, vec2( 1.0,  0.0) - pp, dir, len2, lob, clp, gC);
    FsrEasuTapF(aC, aW, vec2( 1.0,  2.0) - pp, dir, len2, lob, clp, oC);
    FsrEasuTapF(aC, aW, vec2( 0.0,  2.0) - pp, dir, len2, lob, clp, nC);
    pix = min(max4, max(min4, aC / max(aW, 1e-6)));
}

vec3 applyFSR(vec2 uv, float sharp) {
    vec2 inRes  = max(vec2(pc.resW, pc.resH), vec2(1.0));
    vec2 outRes = max(vec2(pc.outW, pc.outH), vec2(1.0));
    vec4 con0, con1, con2, con3;
    FsrEasuCon(con0, con1, con2, con3, inRes, inRes, outRes);
    vec3 pix;
    FsrEasuF(pix, uv * outRes, con0, con1, con2, con3);
    // Light contrast sharpen standing in for a full RCAS second pass.
    vec3 bil = texture(texSampler, uv).rgb;
    return clamp(pix + (pix - bil) * (sharp * 0.5), 0.0, 1.0);
}

// ---- Snapdragon Game Super Resolution 1 (SGSR) ----
// xla: ported from SnapdragonStudios/snapdragon-gsr sgsr/v1/include/glsl/sgsr1_shader_mobile.frag,
// Copyright (c) 2023-2025, Qualcomm Innovation Center, Inc., SPDX-License-Identifier: BSD-3-Clause.
// RGBA mode: one pass, a 12-tap Lanczos-like filter on the green channel (4 textureGathers) plus
// adaptive sharpening, and only on pixels near an edge; built for Adreno. Cheaper than EASU above.
float sgsrFastLanczos2(float x) {
    float wA = x - 4.0;
    float wB = x * wA - wA;
    wA *= wA;
    return wB * wA;
}

vec2 sgsrWeightY(float dx, float dy, float c, float stdDev) {
    float x = ((dx * dx) + (dy * dy)) * 0.55 + clamp(abs(c) * stdDev, 0.0, 1.0);
    float w = sgsrFastLanczos2(x);
    return vec2(w, w * c);
}

vec3 applySGSR(vec2 uv, float sharp) {
    const float edgeThreshold = 8.0 / 255.0;          // SGSR's recommended mobile value
    float edgeSharpness = 1.0 + clamp(sharp, 0.0, 1.0); // SGSR's range is [1, 2], default 2
    vec2 inRes = max(vec2(pc.resW, pc.resH), vec2(1.0));
    vec4 viewportInfo = vec4(1.0 / inRes, inRes);

    vec4 color = vec4(textureLod(texSampler, uv, 0.0).rgb, 0.0);
    // Shown 1:1 or smaller: nothing to reconstruct.
    if (pc.outW <= pc.resW && pc.outH <= pc.resH) return color.rgb;

    vec2 imgCoord = uv * viewportInfo.zw + vec2(-0.5, 0.5);
    vec2 imgCoordPixel = floor(imgCoord);
    vec2 coord = imgCoordPixel * viewportInfo.xy;
    vec2 pl = imgCoord - imgCoordPixel;
    vec4 left = textureGather(texSampler, coord, 1);

    float edgeVote = abs(left.z - left.y) + abs(color.g - left.y) + abs(color.g - left.z);
    if (edgeVote > edgeThreshold) {
        coord.x += viewportInfo.x;

        vec4 right = textureGather(texSampler, coord + vec2(viewportInfo.x, 0.0), 1);
        vec4 upDown;
        upDown.xy = textureGather(texSampler, coord + vec2(0.0, -viewportInfo.y), 1).wz;
        upDown.zw = textureGather(texSampler, coord + vec2(0.0, viewportInfo.y), 1).yx;

        float mean = (left.y + left.z + right.x + right.w) * 0.25;
        left -= vec4(mean);
        right -= vec4(mean);
        upDown -= vec4(mean);
        color.w = color.g - mean;

        float sum = abs(left.x) + abs(left.y) + abs(left.z) + abs(left.w)
                  + abs(right.x) + abs(right.y) + abs(right.z) + abs(right.w)
                  + abs(upDown.x) + abs(upDown.y) + abs(upDown.z) + abs(upDown.w);
        float stdDev = 2.181818 / max(sum, 1e-5);

        vec2 aWY = sgsrWeightY(pl.x, pl.y + 1.0, upDown.x, stdDev);
        aWY += sgsrWeightY(pl.x - 1.0, pl.y + 1.0, upDown.y, stdDev);
        aWY += sgsrWeightY(pl.x - 1.0, pl.y - 2.0, upDown.z, stdDev);
        aWY += sgsrWeightY(pl.x, pl.y - 2.0, upDown.w, stdDev);
        aWY += sgsrWeightY(pl.x + 1.0, pl.y - 1.0, left.x, stdDev);
        aWY += sgsrWeightY(pl.x, pl.y - 1.0, left.y, stdDev);
        aWY += sgsrWeightY(pl.x, pl.y, left.z, stdDev);
        aWY += sgsrWeightY(pl.x + 1.0, pl.y, left.w, stdDev);
        aWY += sgsrWeightY(pl.x - 1.0, pl.y - 1.0, right.x, stdDev);
        aWY += sgsrWeightY(pl.x - 2.0, pl.y - 1.0, right.y, stdDev);
        aWY += sgsrWeightY(pl.x - 2.0, pl.y, right.z, stdDev);
        aWY += sgsrWeightY(pl.x - 1.0, pl.y, right.w, stdDev);

        float finalY = aWY.y / aWY.x;
        float maxY = max(max(left.y, left.z), max(right.x, right.w));
        float minY = min(min(left.y, left.z), min(right.x, right.w));
        finalY = clamp(edgeSharpness * finalY, minY, maxY);

        // Smooth high-contrast input.
        float deltaY = clamp(finalY - color.w, -23.0 / 255.0, 23.0 / 255.0);
        color.rgb = clamp(color.rgb + vec3(deltaY), 0.0, 1.0);
    }
    return color.rgb;
}

vec3 applyDLS(vec2 uv, float sharp) {
    vec2 texel = 1.0 / max(vec2(pc.resW, pc.resH), vec2(1.0));
    float SAT   = 1.0 + sharp * 0.20;
    float CON   = 1.0 + sharp * 0.12;
    float SHARP = sharp * 1.2;

    vec3 orig = texture(texSampler, uv).rgb;
    vec3 c    = clamp((orig - 0.5) * CON + 0.5, 0.0, 1.0);
    float gray = dot(c, vec3(0.299,0.587,0.114));
    c = mix(vec3(gray), c, SAT);

    vec3 blur = (texture(texSampler, uv + vec2( 0.0,    -texel.y)).rgb
               + texture(texSampler, uv + vec2( 0.0,     texel.y)).rgb
               + texture(texSampler, uv + vec2(-texel.x,  0.0   )).rgb
               + texture(texSampler, uv + vec2( texel.x,  0.0   )).rgb) * 0.25;
    return clamp(c + (orig - blur) * SHARP, 0.0, 1.0);
}

vec3 applyCRT(vec2 uv) {
    float CA = 1.0025;
    vec4 fc = texture(texSampler, uv);
    fc.r = texture(texSampler, (uv-0.5)*CA+0.5).r;
    fc.b = texture(texSampler, (uv-0.5)/CA+0.5).b;
    float sx = abs(sin(uv.x*1024.0)*0.5*0.125);
    float sy = abs(sin(uv.y*1024.0)*0.5*0.375);
    return mix(fc.rgb, vec3(0.0), sx+sy);
}

vec3 applyHDR(vec2 uv) {
    vec2 px = 1.0 / max(vec2(pc.resW, pc.resH), vec2(1.0));
    vec3 c = texture(texSampler, uv).rgb;
    float r1=0.793, r2=0.870;
    vec3 b1=vec3(0.0), b2=vec3(0.0);
    vec2 offs[8] = vec2[](vec2(1.5,-1.5),vec2(-1.5,-1.5),vec2(1.5,1.5),vec2(-1.5,1.5),
                          vec2(0.0,-2.5),vec2(0.0,2.5),vec2(-2.5,0.0),vec2(2.5,0.0));
    for(int i=0;i<8;i++){
        b1+=texture(texSampler,uv+offs[i]*r1*px).rgb;
        b2+=texture(texSampler,uv+offs[i]*r2*px).rgb;
    }
    b1*=0.005; b2*=0.010;
    float dist=r2-r1;
    vec3 HDR=(c+(b2-b1))*dist;
    return clamp(pow(abs(HDR+c),vec3(1.30))+HDR, 0.0, 1.0);
}

vec3 applyNatural(vec2 uv) {
    mat3 toYIQ = mat3(0.299, 0.596, 0.212,
                      0.587,-0.275,-0.523,
                      0.114,-0.321, 0.311);
    mat3 toRGB = mat3(1.0, 1.0, 1.0,
                      0.95568806,-0.27158179,-1.10817732,
                      0.61985809,-0.64687381, 1.70506455);
    vec3 c = texture(texSampler, uv).rgb;
    vec3 t = c * toYIQ;
    t = vec3(pow(t.r,1.12), t.g*1.2, t.b*1.2);
    return clamp(t * toRGB, 0.0, 1.0);
}

vec3 applyFXAA(vec2 uv) {
    vec2 texel = 1.0 / max(vec2(pc.resW, pc.resH), vec2(1.0));
    vec3 center = texture(texSampler, uv).rgb;
    vec3 north = texture(texSampler, uv + vec2(0.0, -texel.y)).rgb;
    vec3 south = texture(texSampler, uv + vec2(0.0, texel.y)).rgb;
    vec3 west = texture(texSampler, uv + vec2(-texel.x, 0.0)).rgb;
    vec3 east = texture(texSampler, uv + vec2(texel.x, 0.0)).rgb;
    float centerLum = dot(center, vec3(0.299, 0.587, 0.114));
    float edgeLum = max(max(abs(centerLum - dot(north, vec3(0.299, 0.587, 0.114))),
                            abs(centerLum - dot(south, vec3(0.299, 0.587, 0.114)))),
                        max(abs(centerLum - dot(west, vec3(0.299, 0.587, 0.114))),
                            abs(centerLum - dot(east, vec3(0.299, 0.587, 0.114)))));
    vec3 softened = (center * 4.0 + north + south + west + east) * 0.125;
    return mix(center, softened, smoothstep(0.04, 0.20, edgeLum));
}

vec3 applyColorAdjustments(vec3 color) {
    vec3 adjusted = color + vec3(pc.brightness);
    adjusted = (adjusted - 0.5) * (1.0 + pc.contrast) + 0.5;
    adjusted = pow(clamp(adjusted, 0.0, 1.0), vec3(1.0 / max(pc.gamma, 0.01)));
    return clamp(adjusted, 0.0, 1.0);
}

vec3 applyToon(vec3 color) {
    float levels = 6.0;
    return floor(clamp(color, 0.0, 1.0) * levels + 0.5) / levels;
}

vec3 applyVivid(vec3 color) {
    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    vec3 saturated = mix(vec3(luma), color, 1.25);
    return clamp((saturated - 0.5) * 1.08 + 0.5, 0.0, 1.0);
}

vec3 applyCRTOverlay(vec2 uv, vec3 color) {
    float scanline = 0.86 + 0.14 * sin(uv.y * max(pc.resH, 1.0) * 3.14159265);
    float grille = 0.94 + 0.06 * sin(uv.x * max(pc.resW, 1.0) * 3.14159265);
    return clamp(color * scanline * grille, 0.0, 1.0);
}

vec3 applyNTSC(vec2 uv, vec3 color) {
    vec2 texel = 1.0 / max(vec2(pc.resW, pc.resH), vec2(1.0));
    vec3 shifted = color;
    shifted.r = texture(texSampler, uv + vec2(texel.x * 1.25, 0.0)).r;
    shifted.b = texture(texSampler, uv - vec2(texel.x * 1.25, 0.0)).b;
    float bleed = sin((uv.y * max(pc.resH, 1.0) + uv.x * 24.0) * 0.45) * 0.018;
    return clamp(mix(color, shifted + vec3(bleed), 0.65), 0.0, 1.0);
}

void main() {
    vec2 uv = fragTexCoord;
    vec4 src = texture(texSampler, uv);
    vec3 rgb;

    if (pc.useTexAlpha != 0 || pc.effectId == 0) rgb = src.rgb;
    else if (pc.effectId == 1) rgb = applyFSR    (uv, pc.sharpness);
    else if (pc.effectId == 2) rgb = applyDLS    (uv, pc.sharpness);
    else if (pc.effectId == 3) rgb = applyCRT    (uv);
    else if (pc.effectId == 4) rgb = applyHDR    (uv);
    else if (pc.effectId == 5) rgb = applyNatural(uv);
    else if (pc.effectId == 6) rgb = applySGSR   (uv, pc.sharpness);
    else                       rgb = src.rgb;

    if (pc.useTexAlpha == 0) {
        if (hasEffect(EFFECT_MASK_FXAA)) rgb = applyFXAA(uv);
        rgb = applyColorAdjustments(rgb);
        if (hasEffect(EFFECT_MASK_TOON))  rgb = applyToon(rgb);
        if (hasEffect(EFFECT_MASK_VIVID)) rgb = applyVivid(rgb);
        if (hasEffect(EFFECT_MASK_CRT))   rgb = applyCRTOverlay(uv, rgb);
        if (hasEffect(EFFECT_MASK_NTSC))  rgb = applyNTSC(uv, rgb);
    }

    outColor = vec4(rgb, pc.useTexAlpha != 0 ? src.a : 1.0);
}
