Shader "Hidden/LyricBlur"
{
    // 歌詞の画像（RawImage）を、ピントが外れたようにぼかすためのUI用シェーダー
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Blur ("Blur (0=くっきり, 1=いちばんぼける)", Range(0, 1)) = 0
        _BlurPx ("Blur radius (texture pixels)", Float) = 12
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Blur;
            float _BlurPx;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                if (_Blur < 0.001)
                    return tex2D(_MainTex, i.uv) * i.color;

                // 7x7 のガウスぼかし。透明部分の色が混ざって黒ずまないよう、アルファで重み付けする
                float2 stepUV = _MainTex_TexelSize.xy * _BlurPx * _Blur / 3.0;
                float3 rgb = 0;
                float a = 0;
                float wsum = 0;
                for (int x = -3; x <= 3; x++)
                {
                    for (int y = -3; y <= 3; y++)
                    {
                        float w = exp(-(x * x + y * y) / 8.0);
                        float4 c = tex2D(_MainTex, i.uv + float2(x, y) * stepUV);
                        rgb += c.rgb * c.a * w;
                        a += c.a * w;
                        wsum += w;
                    }
                }
                float outA = a / wsum;
                float3 outRgb = a > 1e-4 ? rgb / a : float3(0, 0, 0);
                return float4(outRgb, outA) * i.color;
            }
            ENDCG
        }
    }
}
