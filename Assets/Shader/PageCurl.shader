Shader "Hidden/PageCurl"
{
    // ページめくり：前のページの画面（_MainTex）が、右下の角から持ち上がってめくれていく
    // _Progress 0 = めくる前（前のページがそのまま）、1 = めくり終わり（新しいページが全部見える）
    Properties
    {
        _MainTex ("Old page", 2D) = "white" {}
        _Progress ("Progress", Range(0, 1)) = 0
        _Dir ("Direction (1=next, -1=prev)", Float) = 1
        _Aspect ("Aspect", Float) = 1.7778
        _FlipY ("Flip Y", Float) = 0
        _PaperColor ("Paper Color", Color) = (0.98, 0.97, 0.95, 1)
        _Shadow ("Shadow", Range(0, 1)) = 0.35
        _ShowThrough ("Back show-through", Range(0, 1)) = 0.12
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Progress, _Dir, _Aspect, _FlipY, _Shadow, _ShowThrough;
            float4 _PaperColor;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 OldPage(float2 p)
            {
                // p は「横 0〜aspect、縦 0〜1」の座標（前のページへ戻るときは左右反転している）
                float2 uv = float2(p.x / _Aspect, p.y);
                if (_Dir < 0) uv.x = 1 - uv.x;
                if (_FlipY > 0.5) uv.y = 1 - uv.y;
                return tex2D(_MainTex, uv);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = float2(i.uv.x * _Aspect, i.uv.y);
                if (_Dir < 0) p.x = _Aspect - p.x;   // 前のページへは、左下の角からめくれる（左右反転して同じ計算）

                // 右下の角から、左上へ向かって折り目が進んでいく
                float2 origin = float2(_Aspect, 0);
                float2 dirV = normalize(float2(-1, 0.42));
                float dMax = dot(float2(0, 1) - origin, dirV);
                float fold = _Progress * dMax * 1.02;
                float d = dot(p - origin, dirV);

                if (d < fold)
                {
                    // めくれて紙がなくなったところ：新しいページが見える。折り目の近くには影が落ちる
                    float sh = _Shadow * saturate(1 - (fold - d) / 0.12) * saturate(fold / 0.05);
                    return float4(0, 0, 0, sh);
                }

                // 折り返された紙（裏側）が、折り目の向こう側に重なっている
                float back = d - fold;
                float2 src = p - dirV * 2 * back;
                bool inside = src.x >= 0 && src.x <= _Aspect && src.y >= 0 && src.y <= 1 && back <= fold;
                if (inside)
                {
                    // 裏側の紙：うっすら表の内容が透けて、折り目の近くは丸まって暗い
                    float4 front = OldPage(src);
                    float3 paper = lerp(_PaperColor.rgb, front.rgb, _ShowThrough);
                    float curve = lerp(0.78, 1.0, saturate(back / 0.18));
                    float edge = lerp(1.0, 0.93, saturate((fold - back) / max(fold, 1e-4)));
                    return float4(paper * curve * edge, 1);
                }

                // まだめくれていない前のページ。めくれている紙の端の外側に、うっすら影
                float4 col = OldPage(p);
                float outside = back - fold;
                float sh2 = _Shadow * 0.6 * saturate(1 - outside / 0.08) * saturate(fold / 0.05) * step(0, outside);
                col.rgb *= 1 - sh2;
                col.a = 1;
                return col;
            }
            ENDCG
        }
    }
}
