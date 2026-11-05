"""Replace AssetRipper's dummy shaders with working approximations.

AssetRipper (free) exports every shader as an opaque placeholder that keeps only the
Properties block. For each dummy shader this keeps the name and Properties (so materials
keep their values) and writes a real SubShader chosen by category:

  tmp          -> TextMeshPro's real shader source (shipped with Unity)
  passthrough  -> full-screen camera effects: copy the source image unchanged
  hidden       -> grab-pass distortion effects (heat haze, refraction): draw nothing
  stencilwrite -> writes the stencil only
  sprite       -> premultiplied alpha sprite with tint, flash, desaturation, UV scrolling,
                  blend mode and stencil support where the material has those properties
                  (the tiled rain, the scrolling fog and the darkness and its cutouts get their own versions)

Shaders it generated before are regenerated too, so it can be run again after changes here.
Files are only rewritten when their contents change (so Unity doesn't reimport them).

Usage: gen_shaders.py <project Assets dir> <out dir> <TMP shader dir>
Writes replacements to <out dir>/<path relative to Assets>. setup passes the Assets dir as <out dir>,
so the dummies are replaced in place.
"""
import os
import re
import shutil
import sys

ASSETS, OUT, TMP = sys.argv[1:4]
GENERATED = "// iOS port: generated replacement for an AssetRipper dummy shader"

PASSTHROUGH = {
    "ImageEffects/Deband", "Custom/Brightness Effect", "Hidden/NoiseAndGrain",
}

# Image effects reimplemented from their published behaviour (Unity Standard Assets) or from the script using them.
SPECIAL = {
    # Per-area colour grading: per-channel curves from a 256x4 ramp, then saturation.
    "Hidden/ColorCorrectionCurvesSimple": """	SubShader {
		Pass {
			ZTest Always Cull Off ZWrite Off
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment frag
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			sampler2D _RgbTex;
			fixed _Saturation;
			fixed4 frag(v2f_img i) : SV_Target
			{
				fixed4 color = tex2D(_MainTex, i.uv);
				fixed3 red = tex2D(_RgbTex, half2(color.r, 0.5 / 4.0)).rgb * fixed3(1, 0, 0);
				fixed3 green = tex2D(_RgbTex, half2(color.g, 1.5 / 4.0)).rgb * fixed3(0, 1, 0);
				fixed3 blue = tex2D(_RgbTex, half2(color.b, 2.5 / 4.0)).rgb * fixed3(0, 0, 1);
				color = fixed4(red + green + blue, color.a);
				fixed lum = Luminance(color.rgb);
				color.rgb = lerp(fixed3(lum, lum, lum), color.rgb, _Saturation);
				return color;
			}
			ENDCG
		}
	}""",
    # The camera's combined post-process (UberPostprocess.cs): each module script that's enabled turns on its keyword
    # and sets its values. Colour curves (per-area colour grading) are the same as ColorCorrectionCurvesSimple;
    # brightness/contrast come from the game's brightness setting and fades. The film grain is left out.
    "Hidden/UberPostprocess": """	SubShader {
		Pass {
			ZTest Always Cull Off ZWrite Off
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment frag
			#pragma multi_compile __ COLOR_CURVES_ENABLED
			#pragma multi_compile __ BRIGHTNESS_EFFECT_ENABLED
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			sampler2D _RgbTex;
			float _Saturation;
			float _Brightness;
			float _Contrast;
			float4 frag(v2f_img i) : SV_Target
			{
				float4 color = tex2D(_MainTex, i.uv);
			#if COLOR_CURVES_ENABLED
				float3 c = saturate(color.rgb);
				float3 graded = float3(tex2D(_RgbTex, float2(c.r, 0.5 / 4.0)).r, tex2D(_RgbTex, float2(c.g, 1.5 / 4.0)).g,
					tex2D(_RgbTex, float2(c.b, 2.5 / 4.0)).b);
				float lum = Luminance(graded);
				color.rgb = lerp(float3(lum, lum, lum), graded, _Saturation);
			#endif
			#if BRIGHTNESS_EFFECT_ENABLED
				color.rgb *= _Brightness;
				color.rgb = (color.rgb - 0.5) * _Contrast + 0.5;
			#endif
				return color;
			}
			ENDCG
		}
	}""",
    # A plane (not a camera effect) that raises the contrast of what's behind it, and with CAN_DESATURATE takes out
    # colour, through its _Mask: the Citadel of Song title screen and the screen-wide desaturation in _GameCameras.
    # As a passthrough it drew its unset _MainTex, a white plane.
    "Custom/Contrast Effect": """	SubShader {
		Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
		GrabPass { "_ContrastGrab" }
		Pass {
			Cull Off ZWrite Off Blend Off
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile __ CAN_DESATURATE
			#include "UnityCG.cginc"
			sampler2D _ContrastGrab;
			sampler2D _Mask;
			float4 _Mask_ST;
			float _Contrast;
			float _Desaturation;
			struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 grab : TEXCOORD1; };
			v2f vert(appdata_base v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.texcoord.xy, _Mask);
				o.grab = ComputeGrabScreenPos(o.pos);
				return o;
			}
			float4 frag(v2f i) : SV_Target
			{
				float4 behind = tex2Dproj(_ContrastGrab, i.grab);
				float3 c = (behind.rgb - 0.5) * _Contrast + 0.5;
			#if CAN_DESATURATE
				c = lerp(c, Luminance(c).xxx, _Desaturation);
			#endif
				return float4(lerp(behind.rgb, saturate(c), tex2D(_Mask, i.uv).r), behind.a);
			}
			ENDCG
		}
	}""",
    # The far background (LightBlurredBackground.cs): a second camera renders everything behind the closest blur plane
    # into a texture, which is blurred and then drawn on that plane in screen space, so it lines up with the view.
    # Drawn before every sprite (as the stand-in used to be), or sprite sorting can put it over the scene.
    "Custom/Screen-Space Blit": """	SubShader {
		Tags { "Queue"="Background" "RenderType"="Opaque" "IgnoreProjector"="True" }
		Pass {
			Cull Off ZWrite Off Blend Off
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			struct v2f { float4 pos : SV_POSITION; float4 screen : TEXCOORD0; };
			v2f vert(appdata_base v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.screen = ComputeScreenPos(o.pos);
				return o;
			}
			fixed4 frag(v2f i) : SV_Target { return tex2D(_MainTex, i.screen.xy / i.screen.w); }
			ENDCG
		}
	}""",
    # Mobile bloom: 0 composite, 1 thresholded downsample, 2/3 vertical/horizontal 7-tap blur.
    "Hidden/FastBloom": """	CGINCLUDE
		#include "UnityCG.cginc"
		sampler2D _MainTex;
		sampler2D _Bloom;
		uniform half4 _MainTex_TexelSize;
		uniform half4 _Parameter;
		static const half curve[7] = { 0.0205, 0.0855, 0.232, 0.324, 0.232, 0.0855, 0.0205 };
		fixed4 fragBloom(v2f_img i) : SV_Target
		{
			return tex2D(_MainTex, i.uv) + tex2D(_Bloom, i.uv);
		}
		fixed4 fragDownsample(v2f_img i) : SV_Target
		{
			half2 o = _MainTex_TexelSize.xy;
			fixed4 c = tex2D(_MainTex, i.uv + o) + tex2D(_MainTex, i.uv + o * half2(-0.5, -0.5))
				+ tex2D(_MainTex, i.uv + o * half2(0.5, -0.5)) + tex2D(_MainTex, i.uv + o * half2(-0.5, 0.5));
			return max(c / 4 - _Parameter.z, 0) * _Parameter.w;
		}
		fixed4 blur(float2 uv, half2 step)
		{
			fixed4 c = 0;
			for (int l = 0; l < 7; l++)
				c += tex2D(_MainTex, uv + step * (l - 3)) * curve[l];
			return c;
		}
		fixed4 fragBlurV(v2f_img i) : SV_Target { return blur(i.uv, _MainTex_TexelSize.xy * half2(0, 1) * _Parameter.x); }
		fixed4 fragBlurH(v2f_img i) : SV_Target { return blur(i.uv, _MainTex_TexelSize.xy * half2(1, 0) * _Parameter.x); }
	ENDCG
	SubShader {
		ZTest Always Cull Off ZWrite Off Blend Off
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragBloom
			ENDCG }
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragDownsample
			ENDCG }
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragBlurV
			ENDCG }
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragBlurH
			ENDCG }
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragBlurV
			ENDCG }
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragBlurH
			ENDCG }
	}""",
    # The light map blur (LightBlur.cs): pass 0 horizontal, pass 1 vertical, _BlurInfo.xy is the texel size.
    "Hollow Knight/Light Blur": """	CGINCLUDE
		#include "UnityCG.cginc"
		sampler2D _MainTex;
		float4 _BlurInfo;
		static const half curve[5] = { 0.0625, 0.25, 0.375, 0.25, 0.0625 };
		fixed4 blur(float2 uv, float2 step)
		{
			fixed4 c = 0;
			for (int l = 0; l < 5; l++)
				c += tex2D(_MainTex, uv + step * (l - 2)) * curve[l];
			return c;
		}
		fixed4 fragH(v2f_img i) : SV_Target { return blur(i.uv, float2(_BlurInfo.x, 0)); }
		fixed4 fragV(v2f_img i) : SV_Target { return blur(i.uv, float2(0, _BlurInfo.y)); }
	ENDCG
	SubShader {
		ZTest Always Cull Off ZWrite Off Blend Off
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragH
			ENDCG }
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragV
			ENDCG }
	}""",
    # Full-screen blur that some rooms fade in (CameraBlurPlane.cs): pass 0 copies, passes 1-4 blur horizontally and
    # vertically in turn, and the last one adds _Vibrancy (saturation). With USE_MASK the blur follows a vignette
    # (_Mask, positioned on Hornet by _Mask_ST), so the middle of the screen stays sharp.
    "UI/Blur/UIBlur Camera": """	CGINCLUDE
		#include "UnityCG.cginc"
		sampler2D _MainTex;
		float4 _MainTex_TexelSize;
		sampler2D _Mask;
		float4 _Mask_ST;
		half _Size;
		half _Vibrancy;
		half _MaskLerp;
		static const half curve[7] = { 0.0205, 0.0855, 0.232, 0.324, 0.232, 0.0855, 0.0205 };
		fixed4 blur(float2 uv, float2 dir)
		{
			half size = _Size;
		#ifdef USE_MASK
			size *= lerp(1, tex2D(_Mask, saturate(uv * _Mask_ST.xy + _Mask_ST.zw)).r, _MaskLerp);
		#endif
			float2 step = dir * _MainTex_TexelSize.xy * size;
			fixed4 c = 0;
			for (int l = 0; l < 7; l++)
				c += tex2D(_MainTex, uv + step * (l - 3)) * curve[l];
			return c;
		}
		fixed4 fragCopy(v2f_img i) : SV_Target { return tex2D(_MainTex, i.uv); }
		fixed4 fragH(v2f_img i) : SV_Target { return blur(i.uv, float2(1, 0)); }
		fixed4 fragV(v2f_img i) : SV_Target { return blur(i.uv, float2(0, 1)); }
		fixed4 fragLast(v2f_img i) : SV_Target
		{
			fixed4 c = blur(i.uv, float2(0, 1));
			fixed lum = Luminance(c.rgb);
			c.rgb = lerp(fixed3(lum, lum, lum), c.rgb, 1 + _Vibrancy);
			return c;
		}
	ENDCG
	SubShader {
		ZTest Always Cull Off ZWrite Off Blend Off
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma multi_compile __ USE_MASK
			#pragma fragment fragCopy
			ENDCG }
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma multi_compile __ USE_MASK
			#pragma fragment fragH
			ENDCG }
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma multi_compile __ USE_MASK
			#pragma fragment fragV
			ENDCG }
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma multi_compile __ USE_MASK
			#pragma fragment fragH
			ENDCG }
		Pass { CGPROGRAM
			#pragma vertex vert_img
			#pragma multi_compile __ USE_MASK
			#pragma fragment fragLast
			ENDCG }
	}""",
}
HIDDEN = {
    "Custom/Heat Effect", "Custom/Heat Effect (Masked)", "Custom/Sprites-Refraction",
    "Custom/Sprites-RefractionGrabber", "UI/Blur/UIBlur", "PS4/Video", "Switch/Video",
}
TMP_SOURCES = {
    "TextMeshPro/Distance Field": "TMP_SDF.shader",
    "TextMeshPro/Mobile/Distance Field": "TMP_SDF-Mobile.shader",
}
TMP_INCLUDES = ["TMPro.cginc", "TMPro_Properties.cginc", "TMPro_Mobile.cginc", "TMPro_Surface.cginc"]


def blend_for(name):
    n = name.lower()
    if any(k in n for k in ("linear dodge", "lineardodge", "additive", "colordodge", "color dodge", "lighten", "lightercolor", "shining")):
        return "One One"  # additive
    if "screen" in n:
        return "OneMinusDstColor One"
    if any(k in n for k in ("multiply", "darken", "darkercolor", "colorburn", "color burn", "linearburn", "linear burn")):
        return "DstColor OneMinusSrcAlpha"  # multiply, premultiplied-safe
    return "One OneMinusSrcAlpha"  # normal premultiplied alpha


def props_of(block):
    return set(re.findall(r"(?m)^\s*(?:\[[^\]]*\]\s*)*(_[A-Za-z0-9_]+)\s*\(", block))


def stencil_block(props):
    if "_Stencil" in props:  # UI mask convention
        return """Stencil {
			Ref [_Stencil]
			Comp [_StencilComp]
			Pass [_StencilOp]
			ReadMask [_StencilReadMask]
			WriteMask [_StencilWriteMask]
		}"""
    if "_StencilRef" in props and "_StencilComp" in props:
        return "Stencil {\n\t\t\tRef [_StencilRef]\n\t\t\tComp [_StencilComp]\n\t\t}"
    return ""


def tiled_masked_subshader(props):
    # Sprites/Tiled (Scrolling) Masked, e.g. the rain in front of the camera: _ScrollTex tiles in world space (scaled by
    # its tiling, scrolling at _SpeedX/Y), and the sprite itself (usually a soft white blob) only fades it out at the edges.
    scroll = "p += float2(_SpeedX, _SpeedY) * _Time.y;" if "_SpeedX" in props and "_SpeedY" in props else ""
    speed = "float _SpeedX; float _SpeedY;" if scroll else ""
    # _WorldOffsetZ is left out: offsetting by world z only shifts the pattern of things that stand still, but slides
    # it a long way across things that move in depth (the webs pulsing with the cocoon in the silk heart memories).
    # The updrafts set LOCAL_SPACE_X: their texture is clamped across, so it has to be laid out across the sprite itself.
    local = ""
    if "_UseLocalSpaceX" in props:
        local += "\n\t\t\t\t#ifdef LOCAL_SPACE_X\n\t\t\t\tp.x = v.vertex.x;\n\t\t\t\t#endif"
    if "_UseLocalSpaceY" in props:
        local += "\n\t\t\t\t#ifdef LOCAL_SPACE_Y\n\t\t\t\tp.y = v.vertex.y;\n\t\t\t\t#endif"
    keywords = ("\n\t\t\t#pragma shader_feature_local LOCAL_SPACE_X" if "_UseLocalSpaceX" in props else "") + \
        ("\n\t\t\t#pragma shader_feature_local LOCAL_SPACE_Y" if "_UseLocalSpaceY" in props else "")
    return f"""	SubShader {{
		Tags {{ "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "DisableBatching"="True" }}
		Cull Off
		Lighting Off
		ZWrite Off
		Pass {{
			Blend One OneMinusSrcAlpha
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag{keywords}
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			float4 _MainTex_ST;
			sampler2D _ScrollTex;
			float4 _ScrollTex_ST;
			fixed4 _Color;
			{speed}
			struct appdata {{ float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; }};
			struct v2f {{ float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float2 scroll : TEXCOORD1; }};
			v2f vert(appdata v)
			{{
				v2f o;
				float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				float2 p = worldPos.xy;{local}
				{scroll}
				o.scroll = p * _ScrollTex_ST.xy + _ScrollTex_ST.zw;
				o.color = v.color * _Color;
				return o;
			}}
			fixed4 frag(v2f i) : SV_Target
			{{
				fixed4 c = tex2D(_ScrollTex, i.scroll) * i.color;
				c.a *= tex2D(_MainTex, i.uv).a;
				c.rgb *= c.a;
				return c;
			}}
			ENDCG
		}}
	}}"""


def fog_subshader():
    # Custom/Tiled Scrolling Fog: the fog texture scrolls at _SpeedX/Y texture widths per 20 seconds (_Time.x; the
    # speeds go up to about 6, and per second the fog races and flickers); with USE_HERO_MASK it thins out around Hornet
    # (_HeroWorldPos, set by SetGlobalShaderPos on her) following _HeroMaskTex, a vignette laid over the screen.
    return """	SubShader {
		Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
		Cull Off
		Lighting Off
		ZWrite Off
		Pass {
			Blend One OneMinusSrcAlpha
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile __ USE_HERO_MASK
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			float4 _MainTex_ST;
			sampler2D _HeroMaskTex;
			float4 _HeroMaskTex_ST;
			fixed4 _Color;
			float _SpeedX; float _SpeedY;
			float4 _HeroWorldPos;
			struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
			struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float4 screen : TEXCOORD1; float4 hero : TEXCOORD2; };
			v2f vert(appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex) + float2(_SpeedX, _SpeedY) * _Time.x;
				o.screen = ComputeScreenPos(o.pos);
				o.hero = ComputeScreenPos(mul(UNITY_MATRIX_VP, float4(_HeroWorldPos.xyz, 1)));
				o.color = v.color * _Color;
				return o;
			}
			fixed4 frag(v2f i) : SV_Target
			{
				fixed4 c = tex2D(_MainTex, i.uv) * i.color;
			#ifdef USE_HERO_MASK
				float2 around = i.screen.xy / i.screen.w - i.hero.xy / i.hero.w + 0.5;
				c.a *= tex2D(_HeroMaskTex, TRANSFORM_TEX(around, _HeroMaskTex)).r;
			#endif
				c.rgb *= c.a;
				return c;
			}
			ENDCG
		}
	}"""


def darkness_subshader():
    # Sprites/Darkness Sprite (the vignette and the blackout over hidden areas): a second camera (DarknessCameraEffect.cs)
    # draws cutout sprites into _DarknessCutout, red for the vignette and blue where a hidden area is uncovered, and
    # the darkness turns see-through where its channel is set. The scene border is left as a plain sprite.
    return """	SubShader {
		Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
		Cull Off
		Lighting Off
		ZWrite Off
		Pass {
			Blend One OneMinusSrcAlpha
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile __ IS_VIGNETTE IS_MASK_BLACKOUT
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			float4 _MainTex_ST;
			sampler2D _DarknessCutout;
			fixed4 _Color;
			struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
			struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float4 cut : TEXCOORD1; };
			v2f vert(appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				o.cut = ComputeScreenPos(o.pos); // the darkness camera has the same view as this one
				o.color = v.color * _Color;
				return o;
			}
			fixed4 frag(v2f i) : SV_Target
			{
				fixed4 c = tex2D(_MainTex, i.uv) * i.color;
			#if defined(IS_VIGNETTE) || defined(IS_MASK_BLACKOUT)
				fixed4 cut = tex2D(_DarknessCutout, i.cut.xy / i.cut.w);
			#endif
			#ifdef IS_VIGNETTE
				c.a *= 1 - cut.r;
			#endif
			#ifdef IS_MASK_BLACKOUT
				c.a *= 1 - cut.b;
			#endif
				c.rgb *= c.a;
				return c;
			}
			ENDCG
		}
	}"""


def mix_channels_subshader():
    # Sprites/Default Mix Channels: the cutout sprites drawn into the darkness texture (see darkness_subshader). Each one
    # sets only its own channels (red for the vignette, blue for an uncovered hidden area; black ones set alpha, the
    # scene border's channel), so they combine by taking the maximum instead of covering each other.
    return """	SubShader {
		Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
		Cull Off
		Lighting Off
		ZWrite Off
		Pass {
			BlendOp Max
			Blend One One
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			float4 _MainTex_ST;
			fixed4 _Color;
			struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
			struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
			v2f vert(appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				o.color = v.color * _Color;
				return o;
			}
			fixed4 frag(v2f i) : SV_Target
			{
				fixed strength = tex2D(_MainTex, i.uv).a * i.color.a;
				fixed3 rgb = i.color.rgb;
				return fixed4(rgb * strength, strength * (1 - max(rgb.r, max(rgb.g, rgb.b))));
			}
			ENDCG
		}
	}"""


def sprite_subshader(name, props):
    if name == "Sprites/Darkness Sprite":
        return darkness_subshader()
    if name == "Sprites/Default Mix Channels":
        return mix_channels_subshader()
    if "_ScrollTex" in props and "tiled" in name.lower() and "masked" in name.lower():
        return tiled_masked_subshader(props)
    if name == "Custom/Tiled Scrolling Fog":
        return fog_subshader()
    decl, uv, tint, post = [], [], [], []
    if "_Color" in props:
        decl.append("fixed4 _Color;")
        tint.append("o.color *= _Color;")
    if "_TintColor" in props:
        decl.append("fixed4 _TintColor;")
    # Scrolling textures: Speed on the main texture.
    if "_SpeedX" in props and "_SpeedY" in props and "_ScrollTex" not in props:
        decl.append("float _SpeedX; float _SpeedY;")
        uv.append("o.uv += float2(_SpeedX, _SpeedY) * _Time.y;")
    if "_Speed" in props:
        decl.append("float _Speed;")
        uv.append("o.uv.x += _Speed * _Time.y;")
    # Grass sway: gentle horizontal sway that grows with height in the sprite.
    if "_SwaySpeed" in props and "_SwayAmount" in props:
        decl.append("float _SwaySpeed; float _SwayAmount;")
        sway = "v.vertex.x += sin(_Time.y * _SwaySpeed + worldPos.x) * _SwayAmount * saturate(v.uv.y) * 0.1;"
    else:
        sway = ""
    if "_FlashAmount" in props and "_FlashColor" in props:
        decl.append("fixed4 _FlashColor; float _FlashAmount;")
        post.append("c.rgb = lerp(c.rgb, _FlashColor.rgb, _FlashAmount);")
    if "_Desaturation" in props:
        decl.append("float _Desaturation;")
        post.append("c.rgb = lerp(c.rgb, dot(c.rgb, float3(0.3, 0.59, 0.11)).xxx, saturate(_Desaturation));")
    if "_TextureFade" in props:  # UI masks fading
        pass
    # Custom/Texture Masked: greyscale masks (red channel) multiply the alpha when _UseMask/_UseMask2 is on. The boss
    # death mist slides one across itself (by animating _MaskTex_ST) to wipe itself away.
    masks = [(t, u) for t, u in (("_MaskTex", "_UseMask"), ("_MaskTex2", "_UseMask2")) if t in props and u in props] \
        if name == "Custom/Texture Masked" else []
    # Custom/Texture Masked with _UseChannelMultiply: the vertex colour picks a channel of the texture (one pattern
    # per channel) and that becomes the alpha of _Color: the note patterns on the Cogwork cylinder puzzle.
    channel = name == "Custom/Texture Masked" and "_UseChannelMultiply" in props
    n = name.lower()
    if "linear light" in n or "linearlight" in n:
        # Linear light is dst + 2 * src - 1: add the part above mid grey, then subtract the part below it.
        passes = [("Blend One One", "c.rgb = max(c.rgb * 2 - 1, 0) * c.a; c.a = 0;"),
                  ("BlendOp RevSub\n\t\t\tBlend One One", "c.rgb = max(1 - c.rgb * 2, 0) * c.a; c.a = 0;")]
    else:
        passes = [(f"Blend {blend_for(name)}", "c.rgb *= c.a;")]
    tags = '"Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True"'
    colormask = "ColorMask [_ColorMask]" if "_ColorMask" in props else ""
    world = "float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;" if sway else ""
    pass_code = "\n".join(sprite_pass(b, f, decl, uv, tint, post, world, sway, masks, channel) for b, f in passes)
    return f"""	SubShader {{
		Tags {{ {tags} }}
		Cull Off
		Lighting Off
		ZWrite Off
		{colormask}
		{stencil_block(props)}
{pass_code}
	}}"""


def sprite_pass(blend, final, decl, uv, tint, post, world, sway, masks=(), channel=False):
    decl = decl + [f"sampler2D {t}; float4 {t}_ST; float {u};" for t, u in masks]
    fields = "".join(f" float2 mask{n} : TEXCOORD{n + 1};" for n in range(len(masks)))
    mask_uv = "".join(f" o.mask{n} = TRANSFORM_TEX(v.uv, {t});" for n, (t, _) in enumerate(masks))
    mask_post = [f"c.a *= lerp(1, tex2D({t}, i.mask{n}).r, {u});" for n, (t, u) in enumerate(masks)]
    if channel:
        decl = decl + ["float _UseChannelMultiply;"]
        fields += " float4 vcol : TEXCOORD3;"
        mask_uv += " o.vcol = v.color;"
        mask_post = ["c = lerp(c, fixed4(_Color.rgb, _Color.a * dot(tex2D(_MainTex, i.uv), i.vcol)), _UseChannelMultiply);"] + mask_post
    post = mask_post + post
    return f"""		Pass {{
			{blend}
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			float4 _MainTex_ST;
			{' '.join(decl)}
			struct appdata {{ float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; }};
			struct v2f {{ float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0;{fields} }};
			v2f vert(appdata v)
			{{
				v2f o;
				{world}
				{sway}
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);{mask_uv}
				{' '.join(uv)}
				o.color = v.color;
				{' '.join(tint)}
				return o;
			}}
			fixed4 frag(v2f i) : SV_Target
			{{
				fixed4 c = tex2D(_MainTex, i.uv) * i.color;
				{' '.join(post)}
				{final}
				return c;
			}}
			ENDCG
		}}"""


def passthrough_subshader():
    # Several identical passes: Graphics.Blit may ask for any pass index.
    one = """		Pass {
			ZTest Always Cull Off ZWrite Off
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment frag
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			fixed4 frag(v2f_img i) : SV_Target { return tex2D(_MainTex, i.uv); }
			ENDCG
		}"""
    return "\tSubShader {\n" + "\n".join([one] * 8) + "\n\t}"


def hidden_subshader():
    return """	SubShader {
		Tags { "Queue"="Transparent" "RenderType"="Transparent" }
		Pass {
			ColorMask 0
			ZWrite Off
		}
	}"""


def stencilwrite_subshader(props):
    ref = "[_StencilRef]" if "_StencilRef" in props else "1"
    return f"""	SubShader {{
		Tags {{ "Queue"="Transparent-1" "RenderType"="Transparent" }}
		Pass {{
			ColorMask 0
			ZWrite Off
			Cull Off
			Stencil {{ Ref {ref} Comp Always Pass Replace }}
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			struct v2f {{ float4 pos : SV_POSITION; float2 uv : TEXCOORD0; }};
			v2f vert(appdata_base v) {{ v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord.xy; return o; }}
			fixed4 frag(v2f i) : SV_Target {{ clip(tex2D(_MainTex, i.uv).a - 0.1); return 0; }}
			ENDCG
		}}
	}}"""


def main():
    counts = {}
    changed = 0
    tmp_dirs = set()
    for root, _, files in os.walk(ASSETS):
        for f in files:
            if not f.endswith(".shader"):
                continue
            path = os.path.join(root, f)
            text = open(path, encoding="utf-8", errors="ignore").read()
            if "DummyShaderTextExporter" not in text and not text.startswith(GENERATED):
                continue
            m = re.search(r'Shader\s+"([^"]+)"', text)
            name = m.group(1)
            pm = re.search(r"\n\tProperties \{.*?\n\t\}", text, re.S)
            props_block = pm.group(0) if pm else "\n\tProperties {\n\t}"
            props = props_of(props_block)
            rel = os.path.relpath(path, ASSETS)
            dest = os.path.join(OUT, rel)
            os.makedirs(os.path.dirname(dest), exist_ok=True)

            if name in TMP_SOURCES:
                shutil.copy(os.path.join(TMP, TMP_SOURCES[name]), dest)
                tmp_dirs.add(os.path.dirname(rel))
                kind = "tmp"
            else:
                if name in SPECIAL:
                    kind, body = "special", SPECIAL[name]
                elif name in PASSTHROUGH:
                    kind, body = "passthrough", passthrough_subshader()
                elif name in HIDDEN:
                    kind, body = "hidden", hidden_subshader()
                elif name == "Sprites/Stencil-Write":
                    kind, body = "stencilwrite", stencilwrite_subshader(props)
                else:
                    kind, body = "sprite", sprite_subshader(name, props)
                out = f'{GENERATED} ({kind}).\nShader "{name}" {{{props_block}\n{body}\n\tFallback Off\n}}\n'
                if not os.path.exists(dest) or open(dest, encoding="utf-8").read() != out:
                    open(dest, "w", encoding="utf-8").write(out)
                    changed += 1
            counts[kind] = counts.get(kind, 0) + 1

    for d in tmp_dirs:  # TMP shaders include their .cginc files by relative path
        for inc in TMP_INCLUDES:
            shutil.copy(os.path.join(TMP, inc), os.path.join(OUT, d, inc))
    print("generated:", counts, "changed:", changed, "tmp include dirs:", sorted(tmp_dirs))


main()
