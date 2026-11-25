Shader "Hidden/PaintBlot"
{
    // 絵の具の模様（スティックとメーターの下）。ふだんはグレーで、_Amount が上がると色がにじんで広がる
    // いつも少しだけ、水彩がにじんでいるように揺らぐ。紙に「乗算」で重ねる
    Properties
    {
        _MainTex ("Paint (grayscale, transparent)", 2D) = "white" {}
        _Color ("Color", Color) = (0.55, 0.70, 0.90, 1)
        _Amount ("Color amount", Range(0, 1)) = 0
        _Gain ("Color gain", Range(0.5, 6)) = 2.5
        _Wobble ("Wobble", Range(0, 3)) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend DstColor Zero

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Color;
            float _Amount, _Gain, _Wobble;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float hash (float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float vnoise (float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(hash(i), hash(i + float2(1, 0)), f.x),
                            lerp(hash(i + float2(0, 1)), hash(i + float2(1, 1)), f.x), f.y);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float t = _Time.y;

                // ゆっくりした揺らぎで、絵の具がまだ乾ききらずに動いているように見せる
                float2 w = float2(vnoise(i.uv * 7.0 + float2(t * 0.08, 0.0)),
                                  vnoise(i.uv * 7.0 + float2(5.0, t * 0.07))) - 0.5;
                float2 uv = i.uv + w * 0.004 * _Wobble;
                float4 c = tex2D(_MainTex, uv);
                float lum = dot(c.rgb, float3(0.299, 0.587, 0.114));
                float dens = (1.0 - lum) * c.a;
                float breathe = 0.88 + 0.12 * vnoise(i.uv * 3.0 - t * 0.06);
                dens *= lerp(1.0, breathe, saturate(_Wobble));

                // 色は、模様の中をまだらに這うように広がる
                float thr = vnoise(i.uv * 3.5 + 11.0) * 0.7 + 0.15;
                float front = _Amount * 1.2;
                float m = 1.0 - smoothstep(front - 0.15, front, thr);
                float rim = exp(-pow((front - thr) / 0.04, 2.0)) * step(thr, front) * (1.0 - _Amount) * 0.6;

                float3 grey = 1.0 - dens.xxx;
                float3 colored = lerp(float3(1, 1, 1), _Color.rgb, saturate(dens * _Gain * (1.0 + rim)));
                return float4(lerp(grey, colored, m), 1);
            }
            ENDCG
        }
    }
}
