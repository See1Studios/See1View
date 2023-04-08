Shader "See1View/Depth"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" { }
        _Seperate ("Seperate", range(0, 1)) = 0.5
    }
    //Builtin
    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            
            #pragma vertex vert
            #pragma fragment frag
            
            #include "UnityCG.cginc"
			
            sampler2D _MainTex;
            sampler2D _CameraDepthTexture;
            float4 _CameraDepthTexture_TexelSize;
            half _Seperate;

            struct appdata
            {
                float4 vertex: POSITION;
                float2 uv: TEXCOORD0;
            };

            struct v2f
            {
                float2 uv: TEXCOORD0;
                float4 vertex: SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            

            fixed4 frag(v2f i): SV_Target
            {
                float4 col = float4(1, 0, 0, 1);
                if (i.vertex.x > _CameraDepthTexture_TexelSize.z / (1 / _Seperate))
                {
                    float depth = tex2D(_CameraDepthTexture, i.uv).r;
                    col = float4(depth, depth, depth, 1);
                }
                else
                {
                    col = tex2D(_MainTex, i.uv);
                }
                return col;
            }
            ENDCG
            
        }
    }
    //URP
    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            
            #pragma vertex vert
            #pragma fragment frag
        	
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
		  
            sampler2D _MainTex;
            sampler2D _CameraDepthTexture;
            float4 _CameraDepthTexture_TexelSize;
            half _Seperate;

            struct appdata
            {
                float4 vertex: POSITION;
                float2 uv: TEXCOORD0;
            };

            struct v2f
            {
                float2 uv: TEXCOORD0;
                float4 vertex: SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.uv = v.uv;
                return o;
            }
            

            half4 frag(v2f i): SV_Target
            {
                float4 col = float4(1, 0, 0, 1);
                if (i.vertex.x > _CameraDepthTexture_TexelSize.z / (1 / _Seperate))
                {
                    float depth = tex2D(_CameraDepthTexture, i.uv).r;
                    col = float4(depth, depth, depth, 1);
                }
                else
                {
                    col = tex2D(_MainTex, i.uv);
                }
                return col;
            }
            ENDHLSL
            
        }
    }
}

//"Shader \"See1View/Depth\"\n{\nProperties\n{\n_MainTex (\"Texture\", 2D) = \"white\" { }\n_Seperate (\"Seperate\", range(0, 1)) = 0.5\n}\nSubShader\n{\n// No culling or depth\nCull Off ZWrite Off ZTest Always\n\nPass\n{\nCGPROGRAM\n\n#pragma vertex vert\n#pragma fragment frag\n\n#include \"UnityCG.cginc\"\n			\nsampler2D _MainTex;\nsampler2D _CameraDepthTexture;\nfloat4 _CameraDepthTexture_TexelSize;\nhalf _Seperate;\n\nstruct appdata\n{\nfloat4 vertex: POSITION;\nfloat2 uv: TEXCOORD0;\n};\n\nstruct v2f\n{\nfloat2 uv: TEXCOORD0;\nfloat4 vertex: SV_POSITION;\n};\n\nv2f vert(appdata v)\n{\nv2f o;\no.vertex = UnityObjectToClipPos(v.vertex);\no.uv = v.uv;\nreturn o;\n}\n\n\nfixed4 frag(v2f i): SV_Target\n{\nfloat4 col = float4(1, 0, 0, 1);\nif (i.vertex.x > _CameraDepthTexture_TexelSize.z / (1 / _Seperate))\n{\nfloat depth = tex2D(_CameraDepthTexture, i.uv).r;\ncol = float4(depth, depth, depth, 1);\n}\nelse\n{\ncol = tex2D(_MainTex, i.uv);\n}\nreturn col;\n}\nENDCG\n\n}\n}\n}\n");
