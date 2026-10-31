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

Usage: gen_shaders.py <project Assets dir> <out dir> <TMP shader dir>
Writes replacements to <out dir>/<path relative to Assets>. setup passes the Assets dir as <out dir>,
so the dummies are replaced in place.
"""
import os
import re
import shutil
import sys

ASSETS, OUT, TMP = sys.argv[1:4]

PASSTHROUGH = {
    "Hidden/UberPostprocess", "ImageEffects/Deband", "Custom/Brightness Effect",
    "Custom/Contrast Effect", "Custom/Screen-Space Blit", "Hidden/NoiseAndGrain",
}

# Unity Standard Assets image effects: reimplemented from their published behaviour.
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
    if any(k in n for k in ("linear dodge", "lineardodge", "additive", "colordodge", "color dodge", "lighten", "lightercolor", "shining", "linear light", "linearlight")):
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


def sprite_subshader(name, props):
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
    blend = blend_for(name)
    tags = '"Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True"'
    colormask = "ColorMask [_ColorMask]" if "_ColorMask" in props else ""
    world = "float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;" if sway else ""
    return f"""	SubShader {{
		Tags {{ {tags} }}
		Cull Off
		Lighting Off
		ZWrite Off
		Blend {blend}
		{colormask}
		{stencil_block(props)}
		Pass {{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			float4 _MainTex_ST;
			{' '.join(decl)}
			struct appdata {{ float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; }};
			struct v2f {{ float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; }};
			v2f vert(appdata v)
			{{
				v2f o;
				{world}
				{sway}
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				{' '.join(uv)}
				o.color = v.color;
				{' '.join(tint)}
				return o;
			}}
			fixed4 frag(v2f i) : SV_Target
			{{
				fixed4 c = tex2D(_MainTex, i.uv) * i.color;
				{' '.join(post)}
				c.rgb *= c.a;
				return c;
			}}
			ENDCG
		}}
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
    tmp_dirs = set()
    for root, _, files in os.walk(ASSETS):
        for f in files:
            if not f.endswith(".shader"):
                continue
            path = os.path.join(root, f)
            text = open(path, encoding="utf-8", errors="ignore").read()
            if "DummyShaderTextExporter" not in text:
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
                out = f'// iOS port: generated replacement for an AssetRipper dummy shader ({kind}).\nShader "{name}" {{{props_block}\n{body}\n\tFallback Off\n}}\n'
                open(dest, "w", encoding="utf-8").write(out)
            counts[kind] = counts.get(kind, 0) + 1

    for d in tmp_dirs:  # TMP shaders include their .cginc files by relative path
        for inc in TMP_INCLUDES:
            shutil.copy(os.path.join(TMP, inc), os.path.join(OUT, d, inc))
    print("generated:", counts, "tmp include dirs:", sorted(tmp_dirs))


main()
