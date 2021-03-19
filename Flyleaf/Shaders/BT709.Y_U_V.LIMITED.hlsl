Texture2D theTextureY : register(t0);
Texture2D theTextureU : register(t1);
Texture2D theTextureV : register(t2);
SamplerState theSampler : register(s0);
struct PixelShaderInput
{
	float4 pos : SV_POSITION;
	float2 tex : TEXCOORD0;
};
float4 main(PixelShaderInput input) : SV_TARGET
{
	const float3 offset = {-0.0627451017, -0.501960814, -0.501960814};
	const float3 Rcoeff = {1.1644,  0.0000,  1.7927};
	const float3 Gcoeff = {1.1644, -0.2132, -0.5329};
	const float3 Bcoeff = {1.1644,  2.1124,  0.0000};
	float4 Output;
	float3 yuv;
	yuv.x = theTextureY.Sample(theSampler, input.tex).r;
	yuv.y = theTextureU.Sample(theSampler, input.tex).r;
	yuv.z = theTextureV.Sample(theSampler, input.tex).r;
	yuv += offset;
	Output.r = dot(yuv, Rcoeff);
	Output.g = dot(yuv, Gcoeff);
	Output.b = dot(yuv, Bcoeff);
	Output.a = 1.0f;
	return Output;
}