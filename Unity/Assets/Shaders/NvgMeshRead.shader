// Reads a mesh back that is not readable on the CPU: each triangle's three vertices (object position, normal, tangent,
// UV) go to pixels 3 * primitive + j of four float targets. The position's w carries that index + 1, so the pixel order
// does not depend on which way up the target is.
Shader "Justachae/NVG/MeshRead"
{
    SubShader
    {
        Cull Off ZTest Always ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 4.0
            float _Width, _Height;
            struct V { float3 pos : TEXCOORD0; float3 normal : TEXCOORD1; float4 tangent : TEXCOORD2; float2 uv : TEXCOORD3; };
            struct G
            {
                float4 clip : SV_POSITION;
                nointerpolation float4 pos : TEXCOORD0;
                nointerpolation float4 normal : TEXCOORD1;
                nointerpolation float4 tangent : TEXCOORD2;
                nointerpolation float4 uv : TEXCOORD3;
            };
            struct O { float4 pos : SV_Target0; float4 normal : SV_Target1; float4 tangent : SV_Target2; float4 uv : SV_Target3; };

            V vert(float4 pos : POSITION, float3 normal : NORMAL, float4 tangent : TANGENT, float2 uv : TEXCOORD0)
            {
                V o;
                o.pos = pos.xyz;
                o.normal = normal;
                o.tangent = tangent;
                o.uv = uv;
                return o;
            }

            [maxvertexcount(12)]
            void geom(triangle V v[3], uint id : SV_PrimitiveID, inout TriangleStream<G> stream)
            {
                static const float2 corners[4] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(1, 1) };
                for (uint j = 0; j < 3; j++)
                {
                    uint k = id * 3 + j;
                    float2 pixel = float2(k % (uint)_Width, k / (uint)_Width);
                    G g;
                    g.pos = float4(v[j].pos, k + 1);
                    g.normal = float4(v[j].normal, 0);
                    g.tangent = v[j].tangent;
                    g.uv = float4(v[j].uv, 0, 0);
                    for (uint c = 0; c < 4; c++)
                    {
                        float2 t = (pixel + corners[c]) / float2(_Width, _Height);
                        g.clip = float4(t * 2 - 1, 0.5, 1);
                        stream.Append(g);
                    }
                    stream.RestartStrip();
                }
            }

            O frag(G g)
            {
                O o;
                o.pos = g.pos;
                o.normal = g.normal;
                o.tangent = g.tangent;
                o.uv = g.uv;
                return o;
            }
            ENDCG
        }
    }
}
