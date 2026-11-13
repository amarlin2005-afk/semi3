Shader "Hidden/WatercolorStamp"
{
    Properties
    {
        _MainTex ("Brush", 2D) = "white" {}
        _PaperTex ("Paper", 2D) = "white" {}
        _Granulation ("Granulation", Range(0, 1)) = 0.6
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        // 乗算：色を重ねるほど深くなる（水彩の重ね塗り）
        Blend DstColor Zero

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _PaperTex;
            float _Granulation;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                o.uv2 = v.uv2;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float brush = tex2D(_MainTex, i.uv).a;
                float paper = tex2D(_PaperTex, i.uv2).r;
                // 紙の凹凸に顔料がたまる「粒状感」
                float g = lerp(1.0, saturate((paper - 0.3) * 2.0), _Granulation);
                float a = saturate(brush * i.color.a * g);
                float3 col = lerp(float3(1, 1, 1), i.color.rgb, a);
                return float4(col, 1);
            }
            ENDCG
        }
    }
}
