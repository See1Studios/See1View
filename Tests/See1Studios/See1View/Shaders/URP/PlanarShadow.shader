Shader "See1View/URP/PlanarShadow" 
{

	Properties {
		_ShadowColor ("Shadow Color", Color) = (0,0,0,1)
		_PlaneHeight ("Plane Height", Float) = 0
	}

	SubShader {
		Tags {"Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline"}
		
		// shadow color
		Pass {   
			
			ZWrite On
			ZTest LEqual 
			Blend SrcAlpha  OneMinusSrcAlpha
			
			Stencil {
				Ref 0
				Comp Equal
				Pass IncrWrap
				ZFail Keep
			}

			HLSLPROGRAM
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			// User-specified uniforms
			uniform float4 _ShadowColor;
			uniform float _PlaneHeight = 0;

			struct Attributes{
				float4 positionOS : POSITION;
			};

			struct Varyings
			{
				float4 pos	: SV_POSITION;
			};

			Varyings vertPlanarShadow( Attributes v)
			{
				Varyings o;
         	            
				float4 vPosWorld = mul( unity_ObjectToWorld, v.positionOS);
				float4 lightDirection = -normalize(_MainLightPosition); 

				float opposite = vPosWorld.y - _PlaneHeight;
				float cosTheta = -lightDirection.y;	// = lightDirection dot (0,-1,0)
				float hypotenuse = opposite / cosTheta;
				float3 vPos = vPosWorld.xyz + ( lightDirection.xyz * hypotenuse );

				o.pos = mul (UNITY_MATRIX_VP, float4(vPos.x, _PlaneHeight, vPos.z ,1));  
	
				return o;
			}

			float4 fragPlanarShadow( Varyings i)
			{
				return _ShadowColor;
			}
			#pragma vertex vert
			#pragma fragment frag

			Varyings vert( Attributes v)
			{
				return vertPlanarShadow(v);
			}


			half4 frag( Varyings i) : COLOR
			{
				return fragPlanarShadow(i);
			}

			ENDHLSL

		}
	}
}
